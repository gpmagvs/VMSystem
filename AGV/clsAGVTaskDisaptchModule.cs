using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using System.Collections.Generic;
using System.Diagnostics;
using static AGVSystemCommonNet6.TASK.clsTaskDto;
using static VMSystem.TrafficControlCenter;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using AGVSystemCommonNet6.Log;

namespace VMSystem.AGV
{
    /// <summary>
    /// 任務派送模組
    /// </summary>
    public partial class clsAGVTaskDisaptchModule : IAGVTaskDispather
    {
        public delegate clsTrafficState OnNavigationTaskCreateHander(IAGV agv, clsMapPoint[] trajectory);
        public static OnNavigationTaskCreateHander OnAGVSOnlineModeChangedRequest;
        public IAGV agv;
        private PathFinder pathFinder = new PathFinder();
        public bool IsAGVExecutable => agv == null ? false : (agv.main_state == clsEnums.MAIN_STATUS.IDLE | agv.main_state == clsEnums.MAIN_STATUS.Charging) && agv.online_state == clsEnums.ONLINE_STATE.ONLINE;
        private string HttpHost => $"http://{agv.connections.HostIP}:{agv.connections.HostPort}";
        public virtual List<clsTaskDto> taskList
        {
            get
            {
                return dbHelper.GetALLInCompletedTask().FindAll(f => f.State != TASK_RUN_STATUS.FAILURE && f.DesignatedAGVName == agv.Name);
            }
        }
        public clsTaskDto ExecutingTask { get; internal set; } = null;

        public clsAGVSimulation AgvSimulation;

        public List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

        protected TaskDatabaseHelper dbHelper = new TaskDatabaseHelper();
        public clsAGVTaskDisaptchModule()
        {
            TaskAssignWorker();
        }
        public clsAGVTaskDisaptchModule(IAGV agv)
        {
            this.agv = agv;
            TaskAssignWorker();
            AgvSimulation = new clsAGVSimulation(this);
        }

        public void AddTask(clsTaskDto taskDto)
        {

            taskList.Add(taskDto);
        }


        protected virtual void TaskAssignWorker()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000);

                    if (!IsAGVExecutable)
                        continue;

                    if (taskList.Count == 0)
                        continue;

                    if (ExecutingTask != null && (ExecutingTask.State == TASK_RUN_STATUS.NAVIGATING | ExecutingTask.State == TASK_RUN_STATUS.WAIT))
                        continue;
                    //將任務依照優先度排序
                    var taskOrderedByPriority = taskList.OrderByDescending(task => task.Priority);
                    var _ExecutingTask = taskOrderedByPriority.First();

                    if (!BeforeDispatchTaskWorkCheck(_ExecutingTask, out ALARMS alarm_code))
                    {
                        ExecutingTask = _ExecutingTask;
                        AlarmManagerCenter.AddAlarm(alarm_code, ALARM_SOURCE.AGVS);
                        ExecutingTask = null;
                        continue;
                    }
                    ExecutingTask = _ExecutingTask;
                    ExecuteTaskAsync(ExecutingTask);
                    //更新
                    //ExecutingTask.State = TASK_RUN_STATUS.WAIT;
                }
            });
        }


        private Dictionary<string, TASK_RUN_STATUS> ExecutingJobsStates = new Dictionary<string, TASK_RUN_STATUS>();
        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {

            if (executingTask.Action == ACTION_TYPE.Charge && agv.main_state == clsEnums.MAIN_STATUS.Charging)
            {
                agv.AddNewAlarm(ALARMS.GET_CHARGE_TASK_BUT_AGV_CHARGING_ALREADY, source: ALARM_SOURCE.AGVS);
            }
            else
            {

                jobs = CreateAGVActionJobs(executingTask);
                ExecutingJobsStates = jobs.ToDictionary(jb => jb.Task_Simplex, jb => TASK_RUN_STATUS.WAIT);
                for (int i = 0; i < jobs.Count; i++)
                {
                    clsTaskDownloadData job = jobs[i];
                    var trajectory = job.ExecutingTrajecory;
                    if (trajectory.Length == 0)
                        continue;
                    clsTrafficState trafficState = OnAGVSOnlineModeChangedRequest.Invoke(agv, trajectory);
                    if (!trafficState.is_navigatable)
                    {
                        Console.WriteLine($"{agv.Name} 任務暫停:交管中心發現有AGV卡在導航路線上");
                        ExecutingTask.State = TASK_RUN_STATUS.FAILURE;
                        dbHelper.Update(ExecutingTask);
                        taskList.Remove(executingTask);
                        ExecutingTask = null;
                        jobs.Clear();
                        return;
                    }
                    else
                    {
                        if (trafficState.is_path_replaned)
                        {
                            Console.WriteLine($"{agv.Name} 路徑已經被交管中心重新規劃");
                            //  job.ReplanTrajectort(trafficState.Trajectory);
                        }
                    }


                    string json = JsonConvert.SerializeObject(job, Formatting.Indented);
                    clsTaskDto response = agv.simulationMode ? AgvSimulation.ActionRequestHandler(job) : await SendActionRequestAsync(job);
                    ExecutingTask.State = response.State;
                    if (ExecutingTask.State == TASK_RUN_STATUS.FAILURE)
                    {
                        ExecutingTask.State = TASK_RUN_STATUS.FAILURE;
                        ExecutingTask.FailureReason = response.FailureReason;
                        dbHelper.Update(ExecutingTask);
                        break;
                    }
                    dbHelper.Update(ExecutingTask);
                    string task_simplex = job.Task_Simplex;
                    //等待結束或失敗了
                    while (ExecutingJobsStates[task_simplex] != TASK_RUN_STATUS.ACTION_FINISH)
                    {
                        Thread.Sleep(1);
                    }

                    Console.WriteLine($"子任務 {task_simplex} 動作完成");
                }

            }
            Console.WriteLine($"任務 {executingTask.TaskName} 完成");
            ExecutingTask.FinishTime = DateTime.Now;
            ExecutingTask.State = TASK_RUN_STATUS.ACTION_FINISH;
            dbHelper.Update(ExecutingTask);
            taskList.Remove(executingTask);
            ExecutingTask = null;
            jobs.Clear();
        }



        public int TaskFeedback(FeedbackData feedbackData)
        {

            string josn = JsonConvert.SerializeObject(feedbackData, Formatting.Indented);

            bool isJobExist = ExecutingJobsStates.ContainsKey(feedbackData.TaskSimplex);

            if (!isJobExist)
                return 4444;
            else
            {
                TASK_RUN_STATUS state = feedbackData.TaskStatus;
                if (state == TASK_RUN_STATUS.ACTION_FINISH)
                {
                    var simplex_task = jobs.FirstOrDefault(jb => jb.Task_Simplex == feedbackData.TaskSimplex);

                    if (TryGetStationByTag(agv.states.Last_Visited_Node, out MapStation point))
                    {
                        LOG.INFO($"AGV-{agv.Name} Finish Action ({simplex_task.Action_Type}) At Tag-{point.TagNumber}({point.Name})");

                        if (simplex_task.Action_Type == ACTION_TYPE.Load | simplex_task.Action_Type == ACTION_TYPE.Unload) //完成取貨卸貨
                            LDULDFinishReport(simplex_task.Destination, simplex_task.Action_Type == ACTION_TYPE.Load ? 0 : 1);
                    }
                }
                ExecutingJobsStates[feedbackData.TaskSimplex] = state;
                return 0;
            }
        }

        /// <summary>
        /// 
        /// </summary>       
        /// <param name="EQTag">取放貨設備的Tag</param>
        /// <param name="LDULD">0:load , 1:unlod</param>
        private async Task LDULDFinishReport(int EQTag, int LDULD)
        {
            await Http.PostAsync<object, object>($"{Configs.AGVSHost}/api/Task/LDULDFinishFeedback?agv_name={agv.Name}&EQTag={EQTag}&LDULD={LDULD}", null);
        }

        private async Task<clsTaskDto> SendActionRequestAsync(clsTaskDownloadData data)
        {
            try
            {
                clsTaskDto taskStateResponse = await Http.PostAsync<clsTaskDownloadData, clsTaskDto>($"{HttpHost}/api/TaskDispatch/Execute", data);
                return taskStateResponse;
            }
            catch (Exception ex)
            {
                return new clsTaskDto()
                {
                    State = TASK_RUN_STATUS.FAILURE,
                };
            }
        }


        private int FindSecondaryPointTag(MapStation currentStation)
        {
            try
            {
                string stationIndex = currentStation.Target.Keys.First();
                return StaMap.Map.Points[int.Parse(stationIndex)].TagNumber;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private MapStation GetStationByTag(int tag)
        {
            StaMap.TryGetPointByTagNumber(tag, out MapStation station);
            return station;
        }
        private bool TryGetStationByTag(int tag, out MapStation station)
        {
            return StaMap.TryGetPointByTagNumber(tag, out station);
        }

        public void CancelTask()
        {
            if (ExecutingTask != null)
            {
                ExecutingTask = null;
            }
        }
    }
}

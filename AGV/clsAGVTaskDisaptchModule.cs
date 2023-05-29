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

namespace VMSystem.AGV
{
    /// <summary>
    /// 任務派送模組
    /// </summary>
    public class clsAGVTaskDisaptchModule : IAGVTaskDispather
    {
        public delegate clsTrafficState OnNavigationTaskCreateHander(IAGV agv, clsMapPoint[] trajectory);
        public static OnNavigationTaskCreateHander OnAGVSOnlineModeChangedRequest;
        public readonly IAGV agv;
        private PathFinder pathFinder = new PathFinder();
        public bool IsAGVExecutable => (agv.main_state == clsEnums.MAIN_STATUS.IDLE | agv.main_state == clsEnums.MAIN_STATUS.Charging) && agv.online_state == clsEnums.ONLINE_STATE.ONLINE;
        private string HttpHost => $"http://{agv.connections.HostIP}:{agv.connections.HostPort}";
        public List<clsTaskDto> taskList
        {
            get
            {
                return dbHelper.GetALLInCompletedTask().FindAll(f => f.State != TASK_RUN_STATUS.FAILURE && f.DesignatedAGVName == agv.Name);
            }
        }
        public clsTaskDto ExecutingTask { get; internal set; } = null;

        public clsAGVSimulation AgvSimulation;

        public List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

        private TaskDatabaseHelper dbHelper = new TaskDatabaseHelper();

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


        private void TaskAssignWorker()
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
                    ExecutingTask = taskOrderedByPriority.First();
                    ExecuteTaskAsync(ExecutingTask);
                }
            });
        }
        private Dictionary<string, TASK_RUN_STATUS> ExecutingJobsStates = new Dictionary<string, TASK_RUN_STATUS>();
        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {
            ExecutingTask.State = TASK_RUN_STATUS.WAIT;
            if (executingTask.Action == ACTION_TYPE.Charge && agv.main_state == clsEnums.MAIN_STATUS.Charging)
            {
                agv.AddNewAlarm(ALARMS.GET_CHARGE_TASK_BUT_AGV_CHARGING_ALREADY, source: ALARM_SOURCE.AGVS);
            }
            else
            {

                jobs = CreateAGVActionJobs(executingTask);
                ExecutingJobsStates = jobs.ToDictionary(jb => jb.Task_Simplex, jb => TASK_RUN_STATUS.WAIT);
                dbHelper.ModifyFromToStation(ExecutingTask, agv.states.Last_Visited_Node, jobs.Last().ExecutingTrajecory.Last().Point_ID);
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
                        dbHelper.ModifyState(ExecutingTask, TASK_RUN_STATUS.FAILURE);
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
                    clsTaskDto response = agv.simulationMode ? AgvSimulation.ActionRequestHandler(job) : await SendActionRequestAsync(job);
                    ExecutingTask.State = response.State;
                    if (ExecutingTask.State == TASK_RUN_STATUS.FAILURE)
                    {
                        ExecutingTask.State = TASK_RUN_STATUS.FAILURE;
                        ExecutingTask.FailureReason = response.FailureReason;
                        dbHelper.ModifyState(ExecutingTask, TASK_RUN_STATUS.FAILURE);
                        break;
                    }
                    dbHelper.ModifyState(ExecutingTask, ExecutingTask.State);
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
            ExecutingTask.State = TASK_RUN_STATUS.ACTION_FINISH;
            dbHelper.ModifyState(ExecutingTask, TASK_RUN_STATUS.ACTION_FINISH);
            taskList.Remove(executingTask);
            ExecutingTask = null;
            jobs.Clear();
        }

        public int TaskFeedback(FeedbackData feedbackData)
        {
            bool isJobExist = ExecutingJobsStates.ContainsKey(feedbackData.TaskSimplex);

            if (!isJobExist)
                return 4444;
            else
            {
                TASK_RUN_STATUS state = TASK_RUN_STATUS.WAIT;
                int state_code = feedbackData.TaskStatus;
                if (state_code == 0 | state_code == 4)
                    state = TASK_RUN_STATUS.ACTION_FINISH;
                if (state_code == 1 | state_code == 3)
                    state = TASK_RUN_STATUS.NAVIGATING;

                Console.WriteLine($"任務回報-{feedbackData.TaskName}-{feedbackData.TaskSimplex}-{feedbackData.TaskStatus}-Point_Index :{feedbackData.PointIndex}");
                Console.WriteLine($"位置-{agv.states.Last_Visited_Node}");

                ExecutingJobsStates[feedbackData.TaskSimplex] = state;
                return 0;
            }
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

        /// <summary>
        /// 從指派的任務產生對應的移動動作Jobs鍊
        /// </summary>
        /// <returns></returns>
        public List<clsTaskDownloadData> CreateAGVActionJobs(clsTaskDto taskDto)
        {
            List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

            int currentTag = agv.states.Last_Visited_Node;

            MapStation currentStation = GetStationByTag(currentTag); //當前Staion

            if (currentStation == null)
            {
                return new List<clsTaskDownloadData>();
            }

            MapStation destinStation = GetStationByTag(int.Parse(taskDto.To_Station)); //目標Station

            if (currentStation.StationType != 0)//當前位置不是一般點位(可能是STK/EQ/...)
            {
                int destinationTag = FindSecondaryPointTag(currentStation);
                //退出
                clsTaskDownloadData actionData = CreateDischargeActionTaskJob(taskDto.TaskName, currentTag, destinationTag, jobs.Count);
                jobs.Add(actionData);
                currentTag = destinationTag;
            }

            if (taskDto.Action == ACTION_TYPE.None)
            {
                int destinationTag = int.Parse(taskDto.To_Station);
                clsTaskDownloadData actionData = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, destinationTag, jobs.Count);
                if (actionData.Trajectory.Count() != 0)
                    jobs.Add(actionData);
            }
            else if (taskDto.Action == ACTION_TYPE.Charge)
            {
                int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                //移動到二次定位點
                clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                jobs.Add(moveJob);
                //進去
                clsTaskDownloadData chargeJob = CreateChargeActionTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, jobs.Count);
                jobs.Add(chargeJob);

            }
            else if (taskDto.Action == ACTION_TYPE.Load)
            {
                int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                //移動到二次定位點
                clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                jobs.Add(moveJob);
                //進去
                clsTaskDownloadData chargeJob = CreateLoadTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                jobs.Add(chargeJob);

            }
            else if (taskDto.Action == ACTION_TYPE.Unload)
            {
                int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                //移動到二次定位點
                clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                jobs.Add(moveJob);
                //進去
                clsTaskDownloadData chargeJob = CreateUnLoadTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                jobs.Add(chargeJob);

            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                MapStation FromStation = GetStationByTag(int.Parse(taskDto.From_Station));
                MapStation ToStation = GetStationByTag(int.Parse(taskDto.To_Station));

                int From_SecondaryPointTag = FindSecondaryPointTag(FromStation);
                int To_SecondaryPointTag = FindSecondaryPointTag(ToStation);

                //移動到二次定位點 A
                clsTaskDownloadData move2AJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, From_SecondaryPointTag, jobs.Count);
                jobs.Add(move2AJob);
                //進去
                clsTaskDownloadData unloadJob = CreateUnLoadTaskJob(taskDto.TaskName, From_SecondaryPointTag, FromStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                jobs.Add(unloadJob);

                //移動到二次定位點 B
                clsTaskDownloadData move2BJob = CreateMoveActionTaskJob(taskDto.TaskName, From_SecondaryPointTag, To_SecondaryPointTag, jobs.Count);
                jobs.Add(move2BJob);
                //進去
                clsTaskDownloadData loadJob = CreateLoadTaskJob(taskDto.TaskName, To_SecondaryPointTag, ToStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                jobs.Add(loadJob);

            }
            return jobs;
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


        private clsTaskDownloadData CreateDischargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Discharge,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }
        private clsTaskDownloadData CreateChargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Charge,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }
        private clsTaskDownloadData CreateLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Load,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            return actionData;
        }
        private clsTaskDownloadData CreateUnLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Unload,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            return actionData;
        }
        private clsTaskDownloadData CreateMoveActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.None,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

        private MapStation GetStationByTag(int tag)
        {
            var station = StaMap.Map.Points.FirstOrDefault(station => station.Value.TagNumber == tag);
            if (station.Value != null)
                return station.Value;
            else
                return null;
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

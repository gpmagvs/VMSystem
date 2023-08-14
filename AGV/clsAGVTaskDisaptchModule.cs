using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using System.Collections.Generic;
using System.Diagnostics;
using static AGVSystemCommonNet6.TASK.clsTaskDto;
using static VMSystem.TrafficControl.TrafficControlCenter;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Exceptions;
using RosSharp.RosBridgeClient.MessageTypes.Sensor;
using System.Threading.Tasks;
using AGVSystemCommonNet6.Tools.Database;
using VMSystem.VMS;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.Win32;
using AGVSystemCommonNet6.AGVDispatch.Model;
using System.Timers;
using AGVSystemCommonNet6.DATABASE.Helpers;

namespace VMSystem.AGV
{
    /// <summary>
    /// 任務派送模組
    /// </summary>
    public partial class clsAGVTaskDisaptchModule : IAGVTaskDispather
    {
        public IAGV agv;
        private PathFinder pathFinder = new PathFinder();
        public bool IsAGVExecutable => agv == null ? false : (agv.main_state == clsEnums.MAIN_STATUS.IDLE | agv.main_state == clsEnums.MAIN_STATUS.Charging) && agv.online_state == clsEnums.ONLINE_STATE.ONLINE;
        private string HttpHost => $"http://{agv.options.HostIP}:{agv.options.HostPort}";
        public virtual List<clsTaskDto> taskList
        {
            get
            {
                return TaskDBHelper.GetALLInCompletedTask().FindAll(f => f.State != TASK_RUN_STATUS.FAILURE && f.DesignatedAGVName == agv.Name);
            }
        }
        public clsTaskDto ExecutingTask { get; set; } = null;
        public clsMapPoint[] CurrentTrajectory { get; set; }

        public clsAGVSimulation AgvSimulation;

        public List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

        protected TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();

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
                    try
                    {
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
                        await ExecuteTaskAsync(ExecutingTask);
                    }
                    catch (NoPathForNavigatorException ex)
                    {
                        ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
                        AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_ABORT);
                        ExecutingTask = null;
                    }
                    catch (Exception ex)
                    {
                        ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
                        AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_ABORT);
                        ExecutingTask = null;
                    }

                }
            });
        }

        private MapPoint goalPoint;
        private Dictionary<string, TASK_RUN_STATUS> ExecutingJobsStates = new Dictionary<string, TASK_RUN_STATUS>();
        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {
            var taskName = executingTask.TaskName;
            string currentTaskSimplex = "";
            var action = executingTask.Action;
            int task_seq = 0;
            clsTaskDownloadData _taskRunning = new clsTaskDownloadData();
            var fromPointTagStr = executingTask.From_Station;
            var toPointTagStr = executingTask.To_Station;
            goalPoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == toPointTagStr); //最終目標點

            MapPoint fromPoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == fromPointTagStr);
            MapPoint toPoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == toPointTagStr);
            MapPoint trackingToPoint = toPoint;
            ACTION_TYPE current_ld_uld_action = ACTION_TYPE.Unload;

            while (ExecutingTask.State != TASK_RUN_STATUS.ACTION_FINISH)
            {
                if (agv.currentMapPoint.StationType != STATION_TYPE.Normal)
                {
                    //退出 , 下Discharge任務
                    _taskRunning = await CreateDischargeActionTaskJob(taskName, agv.currentMapPoint, task_seq);
                }
                else
                {
                    //MOVE To Goal 
                    if (agv.currentMapPoint.TagNumber == trackingToPoint.TagNumber) //到點
                    {
                        if (action == ACTION_TYPE.None)
                        {
                            break;
                        }
                        if (action == ACTION_TYPE.Carry)
                        {
                            var lduld_point = current_ld_uld_action == ACTION_TYPE.Unload ? fromPoint : goalPoint;
                            _taskRunning = await CreateLDULDTaskJob(taskName, current_ld_uld_action, lduld_point, int.Parse(ExecutingTask.To_Slot), ExecutingTask.Carrier_ID, task_seq);
                            current_ld_uld_action = ACTION_TYPE.Load;
                            trackingToPoint = StaMap.Map.Points[toPoint.Target.First().Key];
                            goalPoint = trackingToPoint;
                        }
                        else
                        {
                            _taskRunning = await CreateLDULDTaskJob(taskName, action, goalPoint, int.Parse(ExecutingTask.To_Slot), ExecutingTask.Carrier_ID, task_seq);
                            goalPoint = action == ACTION_TYPE.None | action == ACTION_TYPE.Charge | action == ACTION_TYPE.Park ? goalPoint : agv.currentMapPoint;

                        }
                    }
                    else
                    {
                        if (action != ACTION_TYPE.None)
                        {
                            if (action == ACTION_TYPE.Carry)
                            {
                                trackingToPoint = StaMap.Map.Points[current_ld_uld_action == ACTION_TYPE.Unload ? fromPoint.Target.First().Key : toPoint.Target.First().Key];//更新to
                                goalPoint = toPoint;
                            }
                            else
                            {
                                trackingToPoint = StaMap.Map.Points[toPoint.Target.First().Key];
                            }
                        }
                        _taskRunning = await CreateMoveActionTaskJob(taskName, agv.currentMapPoint, trackingToPoint, task_seq);
                        _taskRunning.Trajectory.Last().Theta = toPoint.Direction;

                    }
                }



                TaskDBHelper.Update(ExecutingTask);
                var Trajectory = _taskRunning.ExecutingTrajecory.ToArray();
                clsTaskDto agv_response = null;

                #region MyRegion

                //if (_taskRunning.TrafficInfo.waitPoints.Count != 0)
                //{
                //    Queue<MapPoint> waitPointsQueue = new Queue<MapPoint>();
                //    while (_taskRunning.TrafficInfo.waitPoints.Count != 0)
                //    {
                //        _taskRunning.TrafficInfo.waitPoints.TryDequeue(out MapPoint waitPoint);
                //        var index_of_waitPoint = Trajectory.ToList().IndexOf(Trajectory.First(pt => pt.Point_ID == waitPoint.TagNumber));
                //        var index_of_conflicPoint = index_of_waitPoint + 1;
                //        var conflicPoint = _taskRunning.Trajectory[index_of_conflicPoint];
                //        clsMapPoint[] newTrajectory = new clsMapPoint[index_of_waitPoint + 1];
                //        clsTaskDownloadData subTaskRunning = JsonConvert.DeserializeObject<clsTaskDownloadData>(_taskRunning.ToJson());
                //        subTaskRunning.Trajectory.ToList().CopyTo(0, newTrajectory, 0, newTrajectory.Length);
                //        subTaskRunning.Trajectory = newTrajectory;
                //        subTaskRunning.Task_Sequence = task_seq;
                //        currentTaskSimplex = subTaskRunning.Task_Simplex;
                //        ExecutingJobsStates.Add(currentTaskSimplex, TASK_RUN_STATUS.WAIT);

                //        task_seq += 1;
                //        jobs.Add(subTaskRunning);
                //        LOG.INFO($"{agv.Name} 在 {waitPoint.TagNumber} 等待 {conflicPoint.Point_ID} 淨空");
                //        CurrentTrajectory = subTaskRunning.Trajectory;
                //        var _returnDto = await PostTaskRequestToAGVAsync(subTaskRunning);
                //        if (_returnDto.ReturnCode != RETURN_CODE.OK)
                //        {
                //            ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
                //            break;
                //        }
                //        ChangeTaskStatus(TASK_RUN_STATUS.NAVIGATING);
                //        while (VMSManager.AllAGV.FindAll(agv => agv != this.agv).Any(agv => Trajectory.Select(t => t.Point_ID).Contains(agv.currentMapPoint.TagNumber)))
                //        {
                //            Thread.Sleep(100);
                //            Console.WriteLine($"{agv.Name} - 等待其他AGV 離開  Tag {conflicPoint.Point_ID}");
                //        }
                //        Console.WriteLine($"Tag {conflicPoint.Point_ID} 已淨空，當前位置={agv.currentMapPoint.TagNumber}");
                //        while (agv.currentMapPoint.TagNumber != waitPoint.TagNumber)
                //        {
                //            Thread.Sleep(100);
                //            Console.WriteLine($"{agv.Name} - 等待抵達等待點 Tag= {waitPoint.TagNumber}");
                //        }
                //    }
                //}
                #endregion

                _taskRunning.Trajectory = Trajectory;
                _taskRunning.Task_Sequence = task_seq;
                CurrentTrajectory = Trajectory;
                jobs.Add(_taskRunning);
                currentTaskSimplex = _taskRunning.Task_Simplex;
                ExecutingJobsStates.Add(currentTaskSimplex, TASK_RUN_STATUS.WAIT);
                //發送任務到車載
                var returnDto = await PostTaskRequestToAGVAsync(_taskRunning);

                if (returnDto.ReturnCode != RETURN_CODE.OK)
                {
                    ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
                    break;
                }
                ChangeTaskStatus(TASK_RUN_STATUS.NAVIGATING);
                StartRecordTrjectory();
                while (ExecutingJobsStates[currentTaskSimplex] != TASK_RUN_STATUS.ACTION_FINISH)
                {
                    TASK_RUN_STATUS runStatus = TaskDBHelper.GetTaskStateByID(taskName);
                    if (runStatus != TASK_RUN_STATUS.NAVIGATING && runStatus != TASK_RUN_STATUS.WAIT)
                    {
                        ChangeTaskStatus(runStatus);
                        if (runStatus == TASK_RUN_STATUS.CANCEL)
                        {
                            ChangeTaskStatus(TASK_RUN_STATUS.CANCEL);
                            PostTaskCancelRequestToAGVAsync(new clsCancelTaskCmd
                            {
                                ResetMode = RESET_MODE.CYCLE_STOP,
                                Task_Name = taskName,
                                TimeStamp = DateTime.Now
                            });
                            LOG.WARN($"{agv.Name} - [{action}]任務-[{taskName}] 取消");
                        }
                        return;
                    }
                    Thread.Sleep(1);
                }
                LOG.WARN($"{agv.Name} 子任務 {currentTaskSimplex} 動作完成");
                if (agv.currentMapPoint.TagNumber == goalPoint.TagNumber)
                {
                    break;
                }
                task_seq += 1;
            }
            LOG.WARN($"{agv.Name}  任務 {executingTask.TaskName} 完成");
            ChangeTaskStatus(TASK_RUN_STATUS.ACTION_FINISH, executingTask);
        }


        private void ChangeTaskStatus(TASK_RUN_STATUS status, clsTaskDto RunningTask = null, string failure_reason = "")
        {
            if (ExecutingTask == null)
            {
                return;
            }
            ExecutingTask.State = status;
            if (status == TASK_RUN_STATUS.FAILURE | status == TASK_RUN_STATUS.CANCEL | status == TASK_RUN_STATUS.ACTION_FINISH)
            {
                ExecutingTask.FailureReason = failure_reason;
                CurrentTrajectory = new clsMapPoint[0];
                jobs.Clear();
                if (RunningTask != null)
                    taskList.Remove(RunningTask);
                ExecutingTask.FinishTime = DateTime.Now;
                TaskDBHelper.Update(ExecutingTask);
                ExecutingTask = null;
            }
            else
            {
                TaskDBHelper.Update(ExecutingTask);
            }
        }

        System.Timers.Timer TrajectoryStoreTimer;

        public int TaskFeedback(FeedbackData feedbackData, out string message)
        {
            message = "";
            TASK_RUN_STATUS state = feedbackData.TaskStatus;
            var task_simplex = feedbackData.TaskSimplex;
            var simplex_task = jobs.FirstOrDefault(jb => jb.Task_Simplex == task_simplex);

            if (simplex_task == null)
            {
                message = $"{agv.Name} Task Feedback.  Task_Simplex => {feedbackData.TaskSimplex} Not Exist";
                LOG.INFO(message);
                return (int)RETURN_CODE.NG;
            }
            else
            {
                int returnCode = 0;
                try
                {
                    var currnetMapPoint = StaMap.GetPointByTagNumber(CurrentTrajectory[feedbackData.PointIndex].Point_ID);
                    if (currnetMapPoint.TryUnRegistPoint(agv.Name, out string errMsg))
                    {
                        LOG.WARN($"{agv.Name}  Release Point {currnetMapPoint.Name}");
                    }

                    //LOG.WARN($"{agv.Name} 剩餘路徑: {string.Join("->", agv.RemainTrajectory.Select(pt => pt.Point_ID))}");
                    LOG.INFO($"{agv.Name} Task Feedback.  Task_Simplex => {task_simplex} ,Status=> {state}");


                    if (state == TASK_RUN_STATUS.ACTION_FINISH)
                    {
                        EndReocrdTrajectory();
                        if (TryGetStationByTag(agv.states.Last_Visited_Node, out MapPoint point))
                        {
                            LOG.INFO($"AGV-{agv.Name} Finish Action ({simplex_task.Action_Type}) At Tag-{point.TagNumber}({point.Name})");
                            if (simplex_task.Action_Type == ACTION_TYPE.Load | simplex_task.Action_Type == ACTION_TYPE.Unload) //完成取貨卸貨
                                LDULDFinishReport(simplex_task.Destination, simplex_task.Action_Type == ACTION_TYPE.Load ? 0 : 1);
                        }
                    }
                    else
                    {
                        var nexPt = StaMap.GetPointByTagNumber(agv.RemainTrajectory[agv.RemainTrajectory.Length > 1 ? 1 : 0].Point_ID);
                        if (nexPt.IsRegisted)
                        {
                            returnCode = 1;
                        }
                        else
                        {
                            bool registed = nexPt.TryRegistPoint(agv.Name, out clsMapPoiintRegist regInfo);
                            LOG.WARN($"{agv.Name} {(registed ? "Regist " : "Can't  Regist")} Point {nexPt.Name}");
                            returnCode = 0;
                        }
                    }
                    ExecutingJobsStates[task_simplex] = state;
                    return returnCode;
                }
                catch (Exception ex)
                {
                    LOG.Critical(ex.Message, ex);
                    throw ex;
                }

            }
        }

        private void StartRecordTrjectory()
        {
            TrajectoryStoreTimer = new System.Timers.Timer()
            {
                Interval = 1000
            };
            TrajectoryStoreTimer.Elapsed += TrajectoryStoreTimer_Elapsed;
            TrajectoryStoreTimer.Enabled = true;
        }
        private void EndReocrdTrajectory()
        {
            TrajectoryStoreTimer.Stop();
            TrajectoryStoreTimer.Dispose();
        }

        /// <summary>
        /// 儲存軌跡到資料庫
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TrajectoryStoreTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            StoreTrajectory();
        }
        private void StoreTrajectory()
        {
            if (ExecutingTask == null)
            {
                EndReocrdTrajectory();
                return;
            }
            string taskID = ExecutingTask.TaskName;
            string agvName = agv.Name;
            double x = agv.states.Coordination.X;
            double y = agv.states.Coordination.Y;
            double theta = agv.states.Coordination.Theta;
            TrajectoryDBStoreHelper helper = new TrajectoryDBStoreHelper();
            helper.StoreTrajectory(taskID, agvName, x, y, theta);
        }

        /// <summary>
        /// 
        /// </summary>       
        /// <param name="EQTag">取放貨設備的Tag</param>
        /// <param name="LDULD">0:load , 1:unlod</param>
        private async Task LDULDFinishReport(int EQTag, int LDULD)
        {
            await Http.PostAsync<object, object>($"{AGVSConfigulator.SysConfigs.AGVSHost}/api/Task/LDULDFinishFeedback?agv_name={agv.Name}&EQTag={EQTag}&LDULD={LDULD}", null);
        }

        public async Task<SimpleRequestResponse> PostTaskRequestToAGVAsync(clsTaskDownloadData data)
        {
            try
            {
                if (agv.options.Simulation)
                    return await AgvSimulation.ActionRequestHandler(data);

                SimpleRequestResponse taskStateResponse = await Http.PostAsync<clsTaskDownloadData, SimpleRequestResponse>($"{HttpHost}/api/TaskDispatch/Execute", data);
                return taskStateResponse;
            }
            catch (Exception ex)
            {
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }

        public async Task<SimpleRequestResponse> PostTaskCancelRequestToAGVAsync(clsCancelTaskCmd data)
        {
            try
            {
                SimpleRequestResponse taskStateResponse = await Http.PostAsync<clsCancelTaskCmd, SimpleRequestResponse>($"{HttpHost}/api/TaskDispatch/Cancel", data);
                return taskStateResponse;
            }
            catch (Exception ex)
            {
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }


        private int FindSecondaryPointTag(MapPoint currentStation)
        {
            try
            {
                int stationIndex = currentStation.Target.Keys.First();
                return StaMap.Map.Points[stationIndex].TagNumber;
            }
            catch (Exception ex)
            {
                throw new MapPointNotTargetsException();
            }
        }

        private MapPoint GetStationByTag(int tag)
        {
            StaMap.TryGetPointByTagNumber(tag, out MapPoint station);
            return station;
        }
        private bool TryGetStationByTag(int tag, out MapPoint station)
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

        public void DispatchTrafficTask(clsTaskDownloadData task_download_data)
        {
            var _ExecutingTask = new AGVSystemCommonNet6.TASK.clsTaskDto()
            {
                DesignatedAGVName = agv.Name,
                DispatcherName = "TMC",
                Carrier_ID = task_download_data.CST.FirstOrDefault().CST_ID,
                TaskName = task_download_data.Task_Name,
                Priority = 5,
                Action = task_download_data.Action_Type,
                To_Station = task_download_data.Destination.ToString(),
                RecieveTime = DateTime.Now,
                State = TASK_RUN_STATUS.WAIT
            };
            TaskDBHelper.Add(_ExecutingTask);
        }
    }
}

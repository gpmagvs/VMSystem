﻿using AGVSystemCommonNet6.Alarm;
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
using static AGVSystemCommonNet6.MAP.PathFinder;
using VMSystem.AGV.TaskDispatch;

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

                        if ( (TaskStatusTracker.TaskRunningStatus== TASK_RUN_STATUS.NAVIGATING | TaskStatusTracker.TaskRunningStatus == TASK_RUN_STATUS.WAIT))
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
        public clsPathInfo TrafficInfo { get; set; } = new clsPathInfo();



        private MapPoint goalPoint;
        private Dictionary<string, TASK_RUN_STATUS> ExecutingJobsStates = new Dictionary<string, TASK_RUN_STATUS>();
        public clsAGVTaskTrack TaskStatusTracker { get; set; } = new clsAGVTaskTrack();
        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {
            TaskStatusTracker.Start(agv, executingTask);

            //ChangeTaskStatus(TASK_RUN_STATUS.ac);


            //var taskName = executingTask.TaskName;
            //string currentTaskSimplex = "";
            //var action = executingTask.Action;
            //int task_seq = 0;
            //clsTaskDownloadData _taskRunning = new clsTaskDownloadData();
            //var fromPointTagStr = executingTask.From_Station;
            //var toPointTagStr = executingTask.To_Station;
            //goalPoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == toPointTagStr); //最終目標點
            //MapPoint fromPoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == fromPointTagStr);
            //MapPoint toPoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == toPointTagStr);
            //MapPoint trackingToPoint = toPoint;
            //ACTION_TYPE current_ld_uld_action = ACTION_TYPE.Unload;

            //while (ExecutingTask.State != TASK_RUN_STATUS.ACTION_FINISH)
            //{
            //    if (agv.main_state == clsEnums.MAIN_STATUS.DOWN)
            //    {
            //        LOG.WARN($"{agv.Name} - [{action}]任務-[{taskName}] 失敗..AGV DOWN");
            //        ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
            //        return;
            //    }

            //    if (agv.currentMapPoint.StationType != STATION_TYPE.Normal)
            //    {
            //        //退出 , 下Discharge任務
            //        _taskRunning = await CreateDischargeActionTaskJob(taskName, agv.currentMapPoint, task_seq);
            //    }
            //    else
            //    {
            //        //MOVE To Goal 
            //        if (agv.currentMapPoint.TagNumber == trackingToPoint.TagNumber) //到點
            //        {
            //            if (action == ACTION_TYPE.None)
            //            {
            //                break;
            //            }
            //            if (action == ACTION_TYPE.Carry)
            //            {
            //                var lduld_point = current_ld_uld_action == ACTION_TYPE.Unload ? fromPoint : goalPoint;
            //                _taskRunning = await CreateLDULDTaskJob(taskName, current_ld_uld_action, lduld_point, int.Parse(ExecutingTask.To_Slot), ExecutingTask.Carrier_ID, task_seq);
            //                current_ld_uld_action = ACTION_TYPE.Load;
            //                trackingToPoint = StaMap.Map.Points[toPoint.Target.First().Key];
            //                goalPoint = trackingToPoint;
            //            }
            //            else
            //            {
            //                _taskRunning = await CreateLDULDTaskJob(taskName, action, goalPoint, int.Parse(ExecutingTask.To_Slot), ExecutingTask.Carrier_ID, task_seq);
            //                goalPoint = action == ACTION_TYPE.None | action == ACTION_TYPE.Charge | action == ACTION_TYPE.Park ? goalPoint : agv.currentMapPoint;

            //            }
            //        }
            //        else
            //        {
            //            if (action != ACTION_TYPE.None)
            //            {
            //                if (action == ACTION_TYPE.Carry)
            //                {
            //                    trackingToPoint = StaMap.Map.Points[current_ld_uld_action == ACTION_TYPE.Unload ? fromPoint.Target.First().Key : toPoint.Target.First().Key];//更新to
            //                    goalPoint = toPoint;

            //                }
            //                else
            //                {
            //                    trackingToPoint = StaMap.Map.Points[toPoint.Target.First().Key];
            //                }
            //            }
            //            _taskRunning = await CreateMoveActionTaskJob(taskName, agv.currentMapPoint, trackingToPoint, task_seq);

            //            if (action == ACTION_TYPE.Carry)
            //                _taskRunning.Trajectory.Last().Theta = current_ld_uld_action == ACTION_TYPE.Unload ? fromPoint.Direction : toPoint.Direction;
            //            else
            //                _taskRunning.Trajectory.Last().Theta = toPoint.Direction;


            //        }
            //    }



            //    TaskDBHelper.Update(ExecutingTask);
            //    var Trajectory = _taskRunning.ExecutingTrajecory.ToArray();
            //    TrafficInfo = _taskRunning.TrafficInfo;
            //    clsTaskDto agv_response = null;

            //    #region MyRegion

            //    if (TrafficInfo.waitPoints.Count != 0)
            //    {
            //        Queue<MapPoint> waitPointsQueue = new Queue<MapPoint>();
            //        while (TrafficInfo.waitPoints.Count != 0)
            //        {
            //            TrafficInfo.waitPoints.TryDequeue(out MapPoint waitPoint);
            //            var index_of_waitPoint = Trajectory.ToList().IndexOf(Trajectory.First(pt => pt.Point_ID == waitPoint.TagNumber));
            //            var index_of_conflicPoint = index_of_waitPoint + 1;
            //            var conflicPoint = _taskRunning.Trajectory[index_of_conflicPoint];

            //            var conflicMapPoint = StaMap.GetPointByTagNumber(conflicPoint.Point_ID);

            //            clsMapPoint[] newTrajectory = new clsMapPoint[index_of_waitPoint + 1];
            //            clsTaskDownloadData subTaskRunning = JsonConvert.DeserializeObject<clsTaskDownloadData>(_taskRunning.ToJson());
            //            subTaskRunning.Trajectory.ToList().CopyTo(0, newTrajectory, 0, newTrajectory.Length);
            //            subTaskRunning.Trajectory = newTrajectory;
            //            subTaskRunning.Task_Sequence = task_seq;
            //            currentTaskSimplex = subTaskRunning.Task_Simplex;
            //            ExecutingJobsStates.Add(currentTaskSimplex, TASK_RUN_STATUS.WAIT);

            //            task_seq += 1;
            //            jobs.Add(subTaskRunning);
            //            LOG.INFO($"{agv.Name} 在 {waitPoint.TagNumber} 等待 {conflicMapPoint.TagNumber} 淨空");
            //            CurrentTrajectory = subTaskRunning.Trajectory;
            //            if (newTrajectory.Length > 0)
            //            {
            //                Console.WriteLine($"[{subTaskRunning.Task_Name}-{subTaskRunning.Task_Sequence}] Segment Traj:, " + string.Join("->", subTaskRunning.ExecutingTrajecory.Select(p => p.Point_ID)));
            //                foreach (var pt in subTaskRunning.TrafficInfo.stations)
            //                {
            //                    StaMap.RegistPoint(agv.Name, pt);
            //                }
            //                var _returnDto = await PostTaskRequestToAGVAsync(subTaskRunning);
            //                if (_returnDto.ReturnCode != RETURN_CODE.OK)
            //                {
            //                    ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
            //                    break;
            //                }
            //                ChangeTaskStatus(TASK_RUN_STATUS.NAVIGATING);
            //            }
            //            while (conflicMapPoint.IsRegisted | VMSManager.AllAGV.Any(agv => agv.currentMapPoint.TagNumber == conflicMapPoint.TagNumber))
            //            {
            //                if (waitPoint.TagNumber == agv.currentMapPoint.TagNumber)
            //                {
            //                    waitingInfo.IsWaiting = true;
            //                    waitingInfo.WaitingPoint = conflicMapPoint;
            //                }
            //                TASK_RUN_STATUS runStatus = TaskDBHelper.GetTaskStateByID(taskName);
            //                if (runStatus != TASK_RUN_STATUS.NAVIGATING)
            //                {
            //                    waitingInfo.IsWaiting = false;
            //                    ChangeTaskStatus(runStatus);
            //                    return;
            //                }
            //                Thread.Sleep(1000);
            //                Console.WriteLine($"{agv.Name} - 等 Tag {conflicMapPoint.TagNumber} Release");
            //            }

            //            waitingInfo.IsWaiting = false;
            //            Console.WriteLine($"Tag {waitPoint.TagNumber} 已Release，當前位置={agv.currentMapPoint.TagNumber}");
            //            //剩餘路徑
            //            var currentindex = TrafficInfo.stations.IndexOf(agv.currentMapPoint);
            //            var remian_traj = new MapPoint[TrafficInfo.stations.Count - currentindex];
            //            TrafficInfo.stations.CopyTo(currentindex, remian_traj, 0, remian_traj.Length);
            //            var remain_tags = remian_traj.Select(pt => pt.TagNumber);
            //            var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv.Name != this.agv.Name);
            //            int index = 0;

            //            foreach (var pt in remian_traj)
            //            {
            //                var ocupy_agv = otherAGVList.FirstOrDefault(_agv => _agv.currentMapPoint.TagNumber == pt.TagNumber);
            //                if (ocupy_agv != null)
            //                {
            //                    var tag = ocupy_agv.currentMapPoint.TagNumber;
            //                    var newWaitPt = remian_traj[index - 1];
            //                    LOG.WARN($"新增等待點:{newWaitPt.TagNumber}");
            //                    TrafficInfo.waitPoints.Enqueue(newWaitPt);

            //                }
            //                index++;
            //            }
            //        }
            //    }
            //    #endregion

            //    _taskRunning.Trajectory = Trajectory;
            //    _taskRunning.Task_Sequence = task_seq;
            //    CurrentTrajectory = Trajectory;
            //    jobs.Add(_taskRunning);
            //    currentTaskSimplex = _taskRunning.Task_Simplex;
            //    ExecutingJobsStates.Add(currentTaskSimplex, TASK_RUN_STATUS.WAIT);
            //    //發送任務到車載
            //    var returnDto = await PostTaskRequestToAGVAsync(_taskRunning);

            //    if (returnDto.ReturnCode != RETURN_CODE.OK)
            //    {
            //        ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
            //        break;
            //    }
            //    ChangeTaskStatus(TASK_RUN_STATUS.NAVIGATING);
            //    StartRecordTrjectory();
            //    while (ExecutingJobsStates[currentTaskSimplex] != TASK_RUN_STATUS.ACTION_FINISH)
            //    {
            //        if (agv.main_state == clsEnums.MAIN_STATUS.DOWN)
            //        {
            //            LOG.WARN($"{agv.Name} - [{action}]任務-[{taskName}] 失敗..AGV DOWN");

            //            ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
            //            return;
            //        }
            //        TASK_RUN_STATUS runStatus = TaskDBHelper.GetTaskStateByID(taskName);
            //        if (runStatus != TASK_RUN_STATUS.NAVIGATING && runStatus != TASK_RUN_STATUS.WAIT)
            //        {
            //            ChangeTaskStatus(runStatus);
            //            if (runStatus == TASK_RUN_STATUS.CANCEL)
            //            {
            //                ChangeTaskStatus(TASK_RUN_STATUS.CANCEL);
            //                PostTaskCancelRequestToAGVAsync(new clsCancelTaskCmd
            //                {
            //                    ResetMode = RESET_MODE.CYCLE_STOP,
            //                    Task_Name = taskName,
            //                    TimeStamp = DateTime.Now
            //                });
            //                LOG.WARN($"{agv.Name} - [{action}]任務-[{taskName}] 取消");
            //            }
            //            return;
            //        }
            //        Thread.Sleep(1);
            //    }
            //    LOG.WARN($"{agv.Name} 子任務 {currentTaskSimplex} 動作完成");
            //    if (agv.currentMapPoint.TagNumber == goalPoint.TagNumber)
            //    {
            //        break;
            //    }
            //    task_seq += 1;
            //}
            //LOG.WARN($"{agv.Name}  任務 {executingTask.TaskName} 完成");
            //ChangeTaskStatus(TASK_RUN_STATUS.ACTION_FINISH, executingTask);
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
                TaskStatusTracker.waitingInfo.IsWaiting = false;
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
            TaskStatusTracker.HandleAGVFeedback(feedbackData);
            return 0;
        }

        private void StartRecordTrjectory()
        {
            TrajectoryStoreTimer = new System.Timers.Timer()
            {
                Interval = 500
            };
            TrajectoryStoreTimer.Elapsed += TrajectoryStoreTimer_Elapsed;
            TrajectoryStoreTimer.Enabled = true;
        }
        private void EndReocrdTrajectory()
        {
            TrajectoryStoreTimer?.Stop();
            TrajectoryStoreTimer?.Dispose();
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
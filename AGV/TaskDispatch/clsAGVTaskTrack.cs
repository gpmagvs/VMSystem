using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Timers;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV.TaskDispatch
{
    /// <summary>
    /// 追蹤AGV任務鍊
    /// </summary>
    public class clsAGVTaskTrack
    {
        public IAGV AGV;
        protected TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();

        public clsTaskDto TaskOrder;
        public string TaskName => TaskOrder == null ? "" : TaskOrder.TaskName;
        public ACTION_TYPE TaskAction => TaskOrder == null ? ACTION_TYPE.None : TaskOrder.Action;
        public Queue<clsSegmentTask> SegmentTasksQueue { get; set; } = new Queue<clsSegmentTask>();
        public MapPoint SourcePoint { get; set; }
        public MapPoint DestinePoint { get; set; }
        public MapPoint AGVFinalLocPoint { get; set; }
        public MapPoint AGVFinalLocPoint_Carry_Start { get; set; }
        public MapPoint AGVFinalLocPoint_Carry_End { get; set; }
        private PathFinder pathFinder = new PathFinder();
        public clsWaitingInfo waitingInfo { get; set; } = new clsWaitingInfo();
        public clsPathInfo TrafficInfo { get; set; } = new clsPathInfo();
        public ACTION_TYPE[] TrackingActions { get; private set; } = new ACTION_TYPE[0];
        private int taskSequence = 0;
        public List<int> RemainTags
        {
            get
            {
                try
                {

                    if (TaskOrder == null | TrafficInfo.stations.Count == 0)
                        return new List<int>();

                    var currentindex = TrafficInfo.stations.IndexOf(AGVCurrentPoint);
                    if (currentindex < 0)
                        return new List<int>();
                    var remian_traj = new MapPoint[TrafficInfo.stations.Count - currentindex];
                    TrafficInfo.stations.CopyTo(currentindex, remian_traj, 0, remian_traj.Length);
                    return remian_traj.Select(r => r.TagNumber).ToList();
                }
                catch (Exception ex)
                {
                    return new List<int>();
                }
            }
        }
        private MapPoint AGVCurrentPoint => AGV.currentMapPoint;
        private STATION_TYPE AGVCurrentPointStationType => AGVCurrentPoint.StationType;
        public clsTaskDownloadData TrackingTask { get; private set; }


        private ACTION_TYPE previousCompleteAction = ACTION_TYPE.Unknown;
        private ACTION_TYPE carryTaskCompleteAction = ACTION_TYPE.Unknown;
        public ACTION_TYPE currentActionType { get; private set; } = ACTION_TYPE.Unknown;
        public ACTION_TYPE nextActionType { get; private set; } = ACTION_TYPE.Unknown;
        private CancellationTokenSource taskCancel = new CancellationTokenSource();

        private TASK_RUN_STATUS _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;
        public TASK_RUN_STATUS TaskRunningStatus
        {
            get => _TaskRunningStatus;
            set
            {
                if (_TaskRunningStatus != value)
                {
                    _TaskRunningStatus = value;
                    if (_TaskRunningStatus == TASK_RUN_STATUS.CANCEL)
                    {
                        PostTaskCancelRequestToAGVAsync(RESET_MODE.CYCLE_STOP);
                        CancelOrder();

                    }
                }
            }
        }


        private string HttpHost => $"http://{AGV.options.HostIP}:{AGV.options.HostPort}";
        public clsAGVTaskTrack()
        {

            StartTaskStatusWatchDog();
        }

        private void StartTaskStatusWatchDog()
        {
            Task.Run(async () =>
            {
                MAIN_STATUS agv_status = MAIN_STATUS.Unknown;
                while (true)
                {
                    Thread.Sleep(1);
                    if (TaskOrder == null)
                        continue;

                    TaskRunningStatus = TaskDBHelper.GetTaskStateByID(TaskName);

                }
            });
        }

        public void Start(IAGV AGV, clsTaskDto TaskOrder)
        {
            try
            {
                taskCancel = new CancellationTokenSource();
                taskSequence = 0;
                SegmentTasksQueue = new Queue<clsSegmentTask>();
                waitingInfo.IsWaiting = false;
                previousCompleteAction = ACTION_TYPE.Unknown;
                carryTaskCompleteAction = ACTION_TYPE.Unknown;
                this.TaskOrder = TaskOrder;
                GetSourceAndDestineMapPoint();
                TrackingActions = GetTrackingActions();
                DetermineAGVFinalDestinePoint();
                StartRecordTrjectory();
                LOG.INFO($"{AGV.Name}- {TaskOrder.Action} 訂單開始,動作:{string.Join("->", TrackingActions)}");
                SendTaskToAGV();
            }
            catch (IlleagalTaskDispatchException ex)
            {
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: TaskName, location: AGVCurrentPoint.Name);
            }
        }


        /// <summary>
        /// 處理AGV任務回報
        /// </summary>
        /// <param name="feedbackData"></param>
        public async void HandleAGVFeedback(FeedbackData feedbackData)
        {
            await Task.Delay(200);
            var task_simplex = feedbackData.TaskSimplex;
            var task_status = feedbackData.TaskStatus;

            LOG.INFO($"{AGV.Name} Feedback Task Status:{task_simplex} -{feedbackData.TaskStatus}-pt:{feedbackData.PointIndex}");
            if (AGV.main_state == MAIN_STATUS.DOWN)
            {
                taskCancel.Cancel();
                _ = PostTaskCancelRequestToAGVAsync(RESET_MODE.ABORT);
                AbortOrder();
                return;
            }
            switch (task_status)
            {
                case TASK_RUN_STATUS.NO_MISSION:
                    break;
                case TASK_RUN_STATUS.NAVIGATING:
                    break;
                case TASK_RUN_STATUS.REACH_POINT_OF_TRAJECTORY:
                    break;
                case TASK_RUN_STATUS.ACTION_START:
                    break;
                case TASK_RUN_STATUS.ACTION_FINISH:
                    var orderStatus = IsTaskOrderCompleteSuccess(feedbackData, out clsSegmentTask segmentTask);
                    LOG.INFO($"Task Order Status: {orderStatus.ToJson()}");
                    if (orderStatus.Status == ORDER_STATUS.COMPLETED | orderStatus.Status == ORDER_STATUS.NO_ORDER)
                    {
                        CompleteOrder();
                        return;
                    }
                    if (segmentTask.IsWaitConflicTask)
                    {
                        LOG.INFO($"{AGV.Name} 註冊點等待任務 ACTION_FINISH, Task_Simple:{task_simplex} pt:{feedbackData.PointIndex}");
                    }
                    else
                    {
                        try
                        {
                            SendTaskToAGV();
                        }
                        catch (IlleagalTaskDispatchException ex)
                        {
                            AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: TaskName, location: AGVCurrentPoint.Name);
                        }
                    }
                    break;
                case TASK_RUN_STATUS.WAIT:
                    break;
                case TASK_RUN_STATUS.FAILURE:
                    break;
                case TASK_RUN_STATUS.CANCEL:
                    break;
                default:
                    break;
            }


        }

        /// <summary>
        /// 生成AGV任務模型數據
        /// </summary>
        /// <returns></returns>
        private clsTaskDownloadData GenerateAGVNavitorTask()
        {
            clsTaskDownloadData taskData = null;
            switch (nextActionType)
            {
                case ACTION_TYPE.None: //移動任務
                    taskData = CreateMoveActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, GetDestineTagToMove(out var desitne_point), taskSequence);

                    break;
                case ACTION_TYPE.Unload://取貨任務
                    taskData = CreateLDULDTaskJob(TaskName, ACTION_TYPE.Unload, TaskOrder.Action == ACTION_TYPE.Carry ? SourcePoint : DestinePoint, int.Parse(TaskOrder.To_Slot), TaskOrder.Carrier_ID, taskSequence);
                    break;
                case ACTION_TYPE.Load://放貨任務
                    taskData = CreateLDULDTaskJob(TaskName, ACTION_TYPE.Load, DestinePoint, int.Parse(TaskOrder.To_Slot), TaskOrder.Carrier_ID, taskSequence);
                    break;
                case ACTION_TYPE.Charge://充電任務
                    taskData = CreateChargeActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, DestinePoint.TagNumber, taskSequence, DestinePoint.StationType);
                    break;
                case ACTION_TYPE.Discharge://離開充電站任務
                    taskData = CreateExitWorkStationActionTaskJob(TaskName, AGVCurrentPoint, taskSequence, nextActionType);
                    break;
                case ACTION_TYPE.Park://停車任務
                    taskData = CreateParkingActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, DestinePoint.TagNumber, taskSequence, DestinePoint.StationType);
                    break;
                case ACTION_TYPE.Unpark://離開停車點任務
                    taskData = CreateExitWorkStationActionTaskJob(TaskName, AGVCurrentPoint, taskSequence, nextActionType);
                    break;
                #region Not Use Yet
                //case ACTION_TYPE.Escape:
                //    break;
                //case ACTION_TYPE.Carry:
                //    break;
                //case ACTION_TYPE.LoadAndPark:
                //    break;
                //case ACTION_TYPE.Forward:
                //    break;
                //case ACTION_TYPE.Backward:
                //    break;
                //case ACTION_TYPE.FaB:
                //    break;
                //case ACTION_TYPE.Measure:
                //    break;
                //case ACTION_TYPE.ExchangeBattery:
                //    break;
                //case ACTION_TYPE.Hold:
                //    break;
                //case ACTION_TYPE.Break:
                //    break;
                //case ACTION_TYPE.Unknown:
                //    break;

                #endregion
                default:
                    break;
            }
            if (taskData == null)
                return taskData;

            if (TaskOrder.Action != ACTION_TYPE.None && nextActionType == ACTION_TYPE.None)//非一搬移動任務的訂單
            {
                int finalPointTheta = 0;
                //取得最後一點的停車角度

                if (TaskOrder.Action == ACTION_TYPE.Carry)
                {
                    finalPointTheta = (carryTaskCompleteAction == ACTION_TYPE.Unload ? DestinePoint : SourcePoint).Direction_Secondary_Point;
                }
                else
                    finalPointTheta = DestinePoint.Direction_Secondary_Point;
                taskData.Trajectory.Last().Theta = finalPointTheta;
            }

            //軌跡第一點角度為AGV的角度
            if (taskData.ExecutingTrajecory.Length > 1)
            {

                if (taskData.Action_Type == ACTION_TYPE.None)
                    taskData.Trajectory.First().Theta = AGV.states.Coordination.Theta;
                else
                    taskData.Homing_Trajectory.First().Theta = AGV.states.Coordination.Theta;
            }

            return taskData;

        }


        public ACTION_TYPE[] GetTrackingActions()
        {
            var ordered_action = TaskOrder.Action;
            List<ACTION_TYPE> tracking_actions = new List<ACTION_TYPE>();

            if (AGVCurrentPointStationType != STATION_TYPE.Normal)
            {
                var IsAGVInChargeStation = AGVCurrentPoint.IsChargeAble();
                ACTION_TYPE firstAction = IsAGVInChargeStation ? ACTION_TYPE.Discharge : ACTION_TYPE.Unpark;
                tracking_actions.Add(firstAction);
            }

            switch (ordered_action)
            {
                case ACTION_TYPE.None:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None });
                    break;
                case ACTION_TYPE.Unload:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None, ACTION_TYPE.Unload });
                    break;
                case ACTION_TYPE.LoadAndPark:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None, ACTION_TYPE.Park });
                    break;
                case ACTION_TYPE.Forward:
                    break;
                case ACTION_TYPE.Backward:
                    break;
                case ACTION_TYPE.FaB:
                    break;
                case ACTION_TYPE.Measure:
                    break;
                case ACTION_TYPE.Load:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None, ACTION_TYPE.Load });
                    break;
                case ACTION_TYPE.Charge:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None, ACTION_TYPE.Charge });
                    break;
                case ACTION_TYPE.Carry:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None, ACTION_TYPE.Unload, ACTION_TYPE.None, ACTION_TYPE.Load });
                    break;
                case ACTION_TYPE.Discharge:
                    break;
                case ACTION_TYPE.Escape:
                    break;
                case ACTION_TYPE.Park:
                    tracking_actions.AddRange(new ACTION_TYPE[] { ACTION_TYPE.None, ACTION_TYPE.Park });
                    break;
                case ACTION_TYPE.Unpark:
                    break;
                case ACTION_TYPE.ExchangeBattery:
                    break;
                case ACTION_TYPE.Hold:
                    break;
                case ACTION_TYPE.Break:
                    break;
                case ACTION_TYPE.Unknown:
                    break;
                default:
                    break;
            }
            return tracking_actions.ToArray();
        }
        private async void SendTaskToAGV()
        {
            try
            {
                if (taskCancel.IsCancellationRequested)
                    return;
                if (AGV.main_state == clsEnums.MAIN_STATUS.DOWN)
                {
                    _ = PostTaskCancelRequestToAGVAsync(RESET_MODE.ABORT);
                    AbortOrder();
                    return;
                }

                var _nextActionType = DetermineNextAction();
                if (_nextActionType == ACTION_TYPE.Unknown)
                    return;


                nextActionType = _nextActionType;
                TrackingTask = GenerateAGVNavitorTask();
                LOG.WARN($"{AGV.Name}- {nextActionType} 動作即將開始:目的地:{TrackingTask.Destination}_初始角度:{TrackingTask.ExecutingTrajecory.First().Theta},停車角度:{TrackingTask.ExecutingTrajecory.Last().Theta}");
                //TrackingTask = GetTaskDownloadData();
                if (TrackingTask == null)
                {
                    TrafficInfo = new clsPathInfo();
                    AbortOrder();
                    return;
                }
                TrafficInfo = TrackingTask.TrafficInfo;
                await HandleRegistPoint(TrackingTask.TrafficInfo);
                waitingInfo.IsWaiting = false;
                if (TaskRunningStatus == TASK_RUN_STATUS.CANCEL | TaskRunningStatus == TASK_RUN_STATUS.FAILURE)
                {
                    waitingInfo.IsWaiting = false;
                    TrafficInfo = new clsPathInfo();
                    return;
                }
                AddSegmentTask(TrackingTask, is_use_for_wait_registe_release: false);
                var _returnDto = PostTaskRequestToAGVAsync(TrackingTask);
                if (_returnDto.ReturnCode != RETURN_CODE.OK)
                {
                    //CancelTask();
                    //return;
                }
                taskSequence += 1;

            }
            catch (NoPathForNavigatorException ex)
            {
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code);
                CancelOrder();
            }
            catch (Exception ex)
            {
                AlarmManagerCenter.AddAlarm(ALARMS.SYSTEM_ERROR);
                CancelOrder();
            }
        }
        public enum ORDER_STATUS
        {
            EXECUTING,
            COMPLETED,
            FAILURE,
            NO_ORDER
        }

        public class clsOrderStatus
        {
            public ORDER_STATUS Status = ORDER_STATUS.NO_ORDER;
            public string FailureReason = "";
        }
        /// <summary>
        /// 判斷AGV是否順利完成訂單
        /// </summary>
        /// <returns></returns>
        private clsOrderStatus IsTaskOrderCompleteSuccess(FeedbackData feedbackData, out clsSegmentTask segmentTask)
        {
            segmentTask = null;
            if (TaskOrder == null)
            {
                return new clsOrderStatus
                {
                    Status = ORDER_STATUS.NO_ORDER
                };
            }

            //從追蹤訂單中嘗試取出符合的分段任務
            segmentTask = SegmentTasksQueue.FirstOrDefault(seg => seg.Task_Simple == feedbackData.TaskSimplex);
            if (segmentTask == null)
            {
                PostTaskCancelRequestToAGVAsync(RESET_MODE.ABORT);
                AlarmManagerCenter.AddAlarm(ALARMS.AGV_Task_Feedback_But_Task_Name_Not_Match, level: ALARM_LEVEL.WARNING, Equipment_Name: AGV.Name);
                return new clsOrderStatus
                {
                    Status = ORDER_STATUS.FAILURE,
                    FailureReason = "AGV Task Feedback 的任務名稱與當前追蹤的任務不符"
                };
            }
            previousCompleteAction = segmentTask.taskDownloadToAGV.Action_Type;

            var orderACtion = TaskOrder.Action;
            bool isOrderCompleted = false;
            string msg = string.Empty;
            switch (orderACtion)  //任務訂單的類型
            {
                case ACTION_TYPE.None: //一般移動訂單
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Unload:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.LoadAndPark:
                    break;
                case ACTION_TYPE.Forward:
                    break;
                case ACTION_TYPE.Backward:
                    break;
                case ACTION_TYPE.FaB:
                    break;
                case ACTION_TYPE.Measure:
                    break;
                case ACTION_TYPE.Load:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Charge:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Carry:
                    isOrderCompleted = (previousCompleteAction == ACTION_TYPE.Load) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Discharge:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Escape:
                    break;
                case ACTION_TYPE.Park:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Unpark:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.ExchangeBattery:
                    break;
                case ACTION_TYPE.Hold:
                    break;
                case ACTION_TYPE.Break:
                    break;
                case ACTION_TYPE.Unknown:
                    break;
                default:
                    break;
            }

            return new clsOrderStatus
            {
                Status = isOrderCompleted ? ORDER_STATUS.COMPLETED : ORDER_STATUS.EXECUTING
            };
        }
        private ACTION_TYPE DetermineNextAction()
        {
            if (TaskOrder == null)
            {
                return ACTION_TYPE.Unknown;
            }
            var nextActionIndex = -1;
            if (previousCompleteAction == ACTION_TYPE.Unknown)//0個動作已完成,開始第一個
            {
                nextActionIndex = 0;
            }
            else
            {
                if (TaskOrder.Action == ACTION_TYPE.Carry && (previousCompleteAction == ACTION_TYPE.Load | previousCompleteAction == ACTION_TYPE.Unload))
                {
                    carryTaskCompleteAction = previousCompleteAction;
                }
                var currentActionIndex = -1;

                if (TaskOrder.Action == ACTION_TYPE.Carry)//搬運的時候 要判斷
                {
                    currentActionIndex = carryTaskCompleteAction == ACTION_TYPE.Unload ? TrackingActions.ToList().LastIndexOf(previousCompleteAction) : TrackingActions.ToList().IndexOf(previousCompleteAction);
                }
                else
                {
                    currentActionIndex = TrackingActions.ToList().IndexOf(previousCompleteAction);
                }
                nextActionIndex = currentActionIndex + 1;

                if (currentActionIndex == -1)
                    return ACTION_TYPE.Unknown;
                LOG.INFO($"{AGV.Name}- {previousCompleteAction} 動作結束");


                if (nextActionIndex >= TrackingActions.Length)
                {
                    return ACTION_TYPE.Unknown;
                } //完成所有動作
                  //if (previousCompleteAction == ACTION_TYPE.None)
                  //{
                  //    if (!CheckAGVPose(out string message))
                  //    {
                  //        nextActionIndex = currentActionIndex;
                  //    }
                  //    //TODO Check AGV Position, If AGV Is Not At Destin Point or AGV Pose is incorrect(theata error > 5 , Position error >_ cm)
                  //}
            }
            return TrackingActions[nextActionIndex];
        }
        private bool CheckAGVPose(out string message)
        {
            message = string.Empty;
            var trajectory = SegmentTasksQueue.Last().taskDownloadToAGV.ExecutingTrajecory;
            var finalPt = currentActionType == ACTION_TYPE.Load | currentActionType == ACTION_TYPE.Unload ? trajectory.First() : trajectory.Last();
            var destine_tag = finalPt.Point_ID;

            if (AGVCurrentPoint.TagNumber != destine_tag)
            {
                message = "AGV並未抵達目的地";
                LOG.ERROR($"AGV並未抵達 {destine_tag} ");
                return false;
            }
            var _destinTheta = finalPt.Theta;
            var _agvTheta = AGV.states.Coordination.Theta;

            var theta_error = Math.Abs(_agvTheta - _destinTheta);
            theta_error = theta_error > 180 ? 360 - theta_error : theta_error;
            if (Math.Abs(theta_error) > 10)
            {
                message = $"{AGV.Name} 角度與目的地[{destine_tag}]角度設定誤差>10度({AGV.states.Coordination.Theta}/{_destinTheta})";
                LOG.WARN(message);
                return false;
            }

            return true;
        }

        private void DetermineAGVFinalDestinePoint()
        {
            var actionType = TaskOrder.Action;
            var DestinePoint = StaMap.GetPointByTagNumber(int.Parse(TaskOrder.To_Station));
            var SourcePoint = StaMap.GetPointByTagNumber(int.Parse(TaskOrder.From_Station));
            var NextAGVAction = TrackingActions[SegmentTasksQueue.Count];

            if (NextAGVAction == ACTION_TYPE.None && TaskAction == ACTION_TYPE.None)
            {
                AGVFinalLocPoint = DestinePoint;
            }
            else
            {
                if (actionType == ACTION_TYPE.Carry)
                {
                    AGVFinalLocPoint_Carry_Start = StaMap.GetPointByIndex(SourcePoint.Target.Keys.First());
                    AGVFinalLocPoint_Carry_End = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First());
                }
                else
                {
                    if (actionType == ACTION_TYPE.None)
                    {

                        AGVFinalLocPoint = DestinePoint;
                    }
                    else
                    {
                        AGVFinalLocPoint = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First());

                    }
                }
            }

        }
        private int GetAGVThetaToTurn()
        {
            if (TaskOrder == null)
                return -1;
            if (TaskOrder.Action != ACTION_TYPE.None)
            {
                //二次定位點Tag
                return TaskOrder.Action == ACTION_TYPE.Carry ? (carryTaskCompleteAction == ACTION_TYPE.Unload ? DestinePoint.Direction : SourcePoint.Direction) : DestinePoint.Direction;
            }
            else
            {
                return DestinePoint.Direction;
            }
        }

        /// <summary>
        /// 取得移動任務的終點TAG
        /// </summary>
        /// <returns></returns>
        private int GetDestineTagToMove(out MapPoint Point)
        {
            Point = null;
            if (TaskOrder == null)
                return -1;
            if (TaskOrder.Action != ACTION_TYPE.None)
            {
                if (TaskOrder.Action == ACTION_TYPE.Carry)
                {
                    Point = StaMap.GetPointByIndex((carryTaskCompleteAction == ACTION_TYPE.Unload ? DestinePoint : SourcePoint).Target.Keys.First());
                }
                else
                {
                    //二次定位點Tag
                    if (previousCompleteAction == TaskOrder.Action)
                        Point = DestinePoint;
                    else
                        Point = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First());
                }
                return Point.TagNumber;
            }
            else
            {
                Point = DestinePoint;
                return DestinePoint.TagNumber;
            }
        }

        private async Task HandleRegistPoint(clsPathInfo trafficInfo)
        {
            while (trafficInfo.waitPoints.Count != 0)
            {
                await Task.Delay(1);
                trafficInfo.waitPoints.TryDequeue(out MapPoint waitPoint);
                //if (!waitPoint.IsRegisted)
                //    continue;
                //if (waitPoint.RegistInfo?.RegisterAGVName == AGV.Name)
                //    continue;

                clsTaskDownloadData gotoWaitPointTask = CreateGoToWaitPointTaskAndJoinToSegment(trafficInfo, waitPoint, out MapPoint conflicMapPoint);
                LOG.WARN($"{AGV.Name} will goto wait point:{waitPoint.Name}({waitPoint.TagNumber}) to wait conflic point:{conflicMapPoint.Name}({conflicMapPoint.TagNumber}) release");
                if (gotoWaitPointTask != null)
                {
                    foreach (var pt in gotoWaitPointTask.TrafficInfo.stations)
                    {
                        StaMap.RegistPoint(AGV.Name, pt, out string err_msg);
                    }
                    var _returnDto = PostTaskRequestToAGVAsync(gotoWaitPointTask);
                    if (_returnDto.ReturnCode != RETURN_CODE.OK)
                    {
                        AbortOrder();
                        break;
                    }
                    ChangeTaskStatus(TASK_RUN_STATUS.NAVIGATING);
                }
                bool GetConflicPointIsRegistOrNot()
                {
                    if (!conflicMapPoint.RegistInfo.IsRegisted)
                        return false;
                    else
                    {
                        return conflicMapPoint.RegistInfo.RegisterAGVName != AGV.Name;
                    }
                }
                bool IsConflicPointRegisting = GetConflicPointIsRegistOrNot();
                CancellationTokenSource waitAGVReachWaitPtCts = new CancellationTokenSource();

                if (IsConflicPointRegisting)
                {
                    _ = Task.Run(() =>
                    {
                        while (AGVCurrentPoint.TagNumber != waitPoint.TagNumber)
                        {
                            if (waitAGVReachWaitPtCts.IsCancellationRequested)
                                return;
                        }
                        waitingInfo.IsWaiting = true;
                        waitingInfo.WaitingPoint = conflicMapPoint;
                    });
                }
                while (IsConflicPointRegisting = GetConflicPointIsRegistOrNot())
                {
                    Thread.Sleep(1);
                    if (taskCancel.IsCancellationRequested)
                    {
                        return;
                    }
                    TASK_RUN_STATUS runStatus = TaskDBHelper.GetTaskStateByID(TaskName);
                    if (runStatus != TASK_RUN_STATUS.NAVIGATING)
                    {
                        waitingInfo.IsWaiting = false;
                        AbortOrder();
                        return;
                    }
                }
                waitAGVReachWaitPtCts.Cancel();
                Console.WriteLine($"Tag {waitPoint.TagNumber} 已Release，當前位置={AGVCurrentPoint.TagNumber}");

                var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv.Name != this.AGV.Name);
                int index = 0;
                //剩餘路徑
                foreach (var remainTag in RemainTags)
                {
                    var registPt = TrafficControlCenter.DynamicTrafficState.RegistedPoints.FirstOrDefault(rpt => rpt.TagNumber == remainTag);
                    if (registPt != null)
                    {
                        if (!registPt.RegistInfo.IsRegisted)
                            continue;
                        if (registPt.RegistInfo?.RegisterAGVName == AGV.Name)
                            continue;
                        try
                        {
                            LOG.WARN($"剩餘路徑註冊點新增:{registPt.TagNumber}:註冊者:{registPt.RegistInfo.RegisterAGVName}");
                            trafficInfo.waitPoints.Enqueue(registPt);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                    index++;
                }
                LOG.WARN($"等待AGV抵達等待點-{waitPoint.Name}");
                while (!waitingInfo.IsWaiting)
                {
                    Thread.Sleep(1);
                    if (taskCancel.IsCancellationRequested)
                        return;
                }
                taskSequence += 1;
            }
        }

        private clsTaskDownloadData CreateGoToWaitPointTaskAndJoinToSegment(clsPathInfo trafficInfo, MapPoint waitPoint, out MapPoint conflicMapPoint)
        {
            if (TrackingTask == null)
            {
                throw new Exception("CreateGoToWaitPointTaskAndJoinToSegment Fail");
            }
            var oriTrafficInfo = TrackingTask.TrafficInfo;
            clsTaskDownloadData oriTrackingOrderTask = JsonConvert.DeserializeObject<clsTaskDownloadData>(TrackingTask.ToJson());
            var Trajectory = trafficInfo.stations.Select(pt => pt.TagNumber).ToList();
            var index_of_conflicPoint = Trajectory.FindIndex(pt => pt == waitPoint.TagNumber) + 1;
            conflicMapPoint = StaMap.GetPointByTagNumber(Trajectory[index_of_conflicPoint]);
            clsMapPoint[] newTrajectory = new clsMapPoint[index_of_conflicPoint];
            clsTaskDownloadData subTaskRunning = oriTrackingOrderTask;

            try
            {

                oriTrackingOrderTask.ExecutingTrajecory.ToList().CopyTo(0, newTrajectory, 0, newTrajectory.Length);
                newTrajectory.First().Theta = AGV.states.Coordination.Theta;
                subTaskRunning.Trajectory = newTrajectory;
                MapPoint[] newTrackingStations = new MapPoint[newTrajectory.Length];
                trafficInfo.stations.CopyTo(0, newTrackingStations, 0, newTrackingStations.Length);
                subTaskRunning.TrafficInfo.stations = newTrackingStations.ToList();
                AddSegmentTask(subTaskRunning, true);
                string traj_display = string.Join("->", subTaskRunning.ExecutingTrajecory.Select(p => p.Point_ID));
                Console.WriteLine($"New goto wait point task create,{subTaskRunning.Task_Simplex} , Segment Trajetory:{traj_display}");
                return subTaskRunning;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        public async Task<SimpleRequestResponse> PostTaskCancelRequestToAGVAsync(RESET_MODE mode)
        {
            try
            {
                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = mode,
                    Task_Name = TaskName,
                    TimeStamp = DateTime.Now,
                };
                SimpleRequestResponse taskStateResponse = await Http.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"{HttpHost}/api/TaskDispatch/Cancel", reset_cmd);
                LOG.WARN($"取消{AGV.Name}任務-[{mode}]-AGV Response : Return Code :{taskStateResponse.ReturnCode},Message : {taskStateResponse.Message}");
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

        public SimpleRequestResponse PostTaskRequestToAGVAsync(clsTaskDownloadData data)
        {
            try
            {
                if (!Debugger.IsAttached)
                    AGV.CheckAGVStatesBeforeDispatchTask(data.Action_Type, DestinePoint);
                SimpleRequestResponse taskStateResponse = Http.PostAsync<SimpleRequestResponse, clsTaskDownloadData>($"{HttpHost}/api/TaskDispatch/Execute", data).Result;
                return taskStateResponse;
            }
            catch (IlleagalTaskDispatchException ex)
            {
                AbortOrder();
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: TaskName, location: AGVCurrentPoint.Name);
                return new SimpleRequestResponse { ReturnCode = RETURN_CODE.NG, Message = ex.Alarm_Code.ToString() };
            }
            catch (Exception ex)
            {
                LOG.Critical(ex);
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }
        private void AddSegmentTask(clsTaskDownloadData subTaskRunning, bool is_use_for_wait_registe_release)
        {
            SegmentTasksQueue.Enqueue(new clsSegmentTask(this.TaskName, taskSequence, subTaskRunning)
            {
                IsWaitConflicTask = is_use_for_wait_registe_release
            });
        }

        private void GetSourceAndDestineMapPoint()
        {
            string sourceTagStr = TaskOrder.From_Station;
            string destineTagStr = TaskOrder.To_Station;

            SourcePoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == sourceTagStr);
            DestinePoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == destineTagStr);

        }
        private clsTaskDownloadData CreateMoveActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            currentActionType = ACTION_TYPE.None;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.None,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }
        private clsTaskDownloadData CreateChargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {

            currentActionType = ACTION_TYPE.Charge;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Charge,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            actionData.TrafficInfo = pathPlanDto;
            return actionData;
        }
        private clsTaskDownloadData CreateParkingActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {

            currentActionType = ACTION_TYPE.Park;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Park,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            actionData.TrafficInfo = pathPlanDto;
            return actionData;
        }

        private clsTaskDownloadData CreateExitWorkStationActionTaskJob(string taskName, MapPoint currentMapPoint, int task_seq, ACTION_TYPE aCTION_TYPE = ACTION_TYPE.Discharge)
        {
            currentActionType = aCTION_TYPE;
            MapPoint next_point = StaMap.Map.Points[currentMapPoint.Target.First().Key];
            int fromTag = currentMapPoint.TagNumber;
            int toTag = next_point.TagNumber;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = aCTION_TYPE,
                Destination = toTag,
                Task_Name = taskName,
                Station_Type = 0,
                Task_Sequence = task_seq,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }

        private clsTaskDownloadData CreateLDULDTaskJob(string TaskName, ACTION_TYPE Action, MapPoint EQPoint, int to_slot, string cstID, int TaskSeq)
        {
            currentActionType = Action;
            var fromTag = StaMap.Map.Points[EQPoint.Target.First().Key].TagNumber;
            var pathPlanDto = OptimizdPathFind(fromTag, EQPoint.TagNumber);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = Action,
                Destination = EQPoint.TagNumber,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = EQPoint.StationType,
                Task_Sequence = TaskSeq,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            actionData.TrafficInfo = pathPlanDto;
            return actionData;
        }

        private clsPathInfo OptimizdPathFind(int fromTag, int toTag)
        {
            var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != this.AGV);
            clsPathInfo pathInfo = new clsPathInfo();
            StaMap.TryGetPointByTagNumber(toTag, out MapPoint FinalPoint);
            var regitedPoints = TrafficControlCenter.DynamicTrafficState.RegistedPoints;
            var toAvoidPointsTags = regitedPoints.FindAll(pt => pt.RegistInfo != null).FindAll(pt => pt.RegistInfo?.RegisterAGVName != AGV.Name && pt.TagNumber != AGVCurrentPoint.TagNumber).Select(pt => pt.TagNumber).ToList();
            toAvoidPointsTags.AddRange(otherAGVList.Select(agv => agv.states.Last_Visited_Node));//考慮移動路徑
                                                                                                 //toAvoidPointsTags = toAvoidPointsTags.FindAll(pt => !otherAGVList.Select(agv => agv.currentMapPoint.TagNumber).Contains(pt));


            var option = new PathFinderOption
            {
                ConstrainTags = toAvoidPointsTags
            };

            option.ConstrainTags = option.ConstrainTags.Distinct().ToList();

            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag, option);//考慮AGV阻擋下，最短路徑

            if (pathPlanDto == null) //沒有任何路線可以行走
            {
                var shortestPathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);//不考慮AGV阻擋下，最短路徑
                if (shortestPathPlanDto == null)
                {

                    throw new NoPathForNavigatorException();
                }

                var pathTags = shortestPathPlanDto.stations.Select(pt => pt.TagNumber).ToList();
                Dictionary<int, MapPoint> waitPointsDict = new Dictionary<int, MapPoint>();
                foreach (var conflicTag in option.ConstrainTags)
                {
                    var index = pathTags.IndexOf(conflicTag);
                    var waitPtIndex = index - 1;
                    if (waitPtIndex >= 0)
                    {
                        waitPointsDict.Add(index, shortestPathPlanDto.stations[waitPtIndex]);
                    }
                }
                var waitPoints = waitPointsDict.OrderBy(kp => kp.Key).Select(kp => kp.Value);


                foreach (var pt in waitPoints)
                {
                    pathInfo.waitPoints.Enqueue(pt);
                }
                pathInfo.stations = shortestPathPlanDto.stations;
                return pathInfo;
            }
            else
            {
                foreach (var pt in pathPlanDto.stations)
                {
                    StaMap.RegistPoint(AGV.Name, pt, out string err_msg);
                }
                return pathPlanDto;
            }
        }
        internal void ChangeTaskStatus()
        {
            var status = TASK_RUN_STATUS.FAILURE;
            if (previousCompleteAction == ACTION_TYPE.Unknown)
                return;

            if (TaskOrder.Action == ACTION_TYPE.Carry)
            {
                status = previousCompleteAction == ACTION_TYPE.Load ? TASK_RUN_STATUS.ACTION_FINISH : TASK_RUN_STATUS.FAILURE;
            }
            else
                status = previousCompleteAction == TaskOrder.Action ? TASK_RUN_STATUS.ACTION_FINISH : TASK_RUN_STATUS.FAILURE;
            ChangeTaskStatus(status, $"");

        }
        internal void ChangeTaskStatus(TASK_RUN_STATUS status, string failure_reason = "")
        {
            if (TaskOrder == null)
                return;
            TaskOrder.State = status;
            if (status == TASK_RUN_STATUS.FAILURE | status == TASK_RUN_STATUS.CANCEL | status == TASK_RUN_STATUS.ACTION_FINISH)
            {
                EndReocrdTrajectory();
                waitingInfo.IsWaiting = false;
                TaskOrder.FailureReason = failure_reason;
                TaskOrder.FinishTime = DateTime.Now;
                TaskDBHelper.Update(TaskOrder);
                TaskOrder = null;
                _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;
            }
            else
            {
                TaskDBHelper.Update(TaskOrder);
            }
        }

        private void CompleteOrder()
        {
            ChangeTaskStatus(TASK_RUN_STATUS.ACTION_FINISH);
            taskCancel.Cancel();
        }
        internal void AbortOrder()
        {
            taskCancel.Cancel();
            ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
        }

        internal void CancelOrder()
        {
            taskCancel.Cancel();
            ChangeTaskStatus(TASK_RUN_STATUS.CANCEL);
        }

        System.Timers.Timer TrajectoryStoreTimer;

        private void StartRecordTrjectory()
        {
            TrajectoryStoreTimer = new System.Timers.Timer()
            {
                Interval = 100
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
            if (TaskOrder == null)
            {
                EndReocrdTrajectory();
                return;
            }
            string taskID = TaskOrder.TaskName;
            string agvName = AGV.Name;
            double x = AGV.states.Coordination.X;
            double y = AGV.states.Coordination.Y;
            double theta = AGV.states.Coordination.Theta;
            TrajectoryDBStoreHelper helper = new TrajectoryDBStoreHelper();
            helper.StoreTrajectory(taskID, agvName, x, y, theta);
        }


    }

    public class clsSegmentTask
    {
        public string Task_Simple { get; set; }
        public bool IsWaitConflicTask { get; set; } = false;
        public clsTaskDownloadData taskDownloadToAGV { get; set; }
        public TASK_RUN_STATUS Run_Status { get; set; } = TASK_RUN_STATUS.WAIT;
        public MapPoint DestinePoint { get; set; }
        public clsSegmentTask(string taskName, int sequence, clsTaskDownloadData taskDownloadToAGV)
        {
            Task_Simple = taskName + $"-{sequence}";
            Sequence = sequence;
            taskDownloadToAGV.Task_Sequence = sequence;
            this.taskDownloadToAGV = taskDownloadToAGV;
            DetermineDestinePoint();
        }

        private void DetermineDestinePoint()
        {
            var actionType = taskDownloadToAGV.Action_Type;
            if (actionType == ACTION_TYPE.None | actionType == ACTION_TYPE.Charge | actionType == ACTION_TYPE.Park | actionType == ACTION_TYPE.Discharge | actionType == ACTION_TYPE.Unpark)
                DestinePoint = StaMap.GetPointByTagNumber(taskDownloadToAGV.Destination);
            else
            {
                var WorkingStationPoint = StaMap.GetPointByTagNumber(taskDownloadToAGV.Destination);
                DestinePoint = StaMap.GetPointByIndex(WorkingStationPoint.Target.Keys.First());
            }
        }

        public int Sequence { get; }



    }

}

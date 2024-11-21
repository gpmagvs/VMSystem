using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Notify;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;
using NLog;
using System.Threading.Tasks;
using VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    /// <summary>
    /// 處理訂單任務
    /// </summary>
    public abstract class OrderHandlerBase : clsTaskDatabaseWriteableAbstract
    {
        public class BufferOrderState
        {
            public OrderHandlerBase orderBase { get; set; }

            public MapPoint bufferFrom { get; set; }

            public MapPoint bufferTo { get; set; }

            public string message { get; set; } = string.Empty;

            public ALARMS returnCode { get; set; } = ALARMS.NONE;

        }


        public abstract ACTION_TYPE OrderAction { get; }
        public Queue<TaskBase> SequenceTaskQueue { get; set; } = new Queue<TaskBase>();
        public Stack<TaskBase> CompleteTaskStack { get; set; } = new Stack<TaskBase>();
        public clsTaskDto OrderData { get; internal set; } = new clsTaskDto();
        public TaskBase RunningTask { get; internal set; } = new MoveToDestineTask();
        protected ManualResetEvent _CurrnetTaskFinishResetEvent = new ManualResetEvent(false);
        private CancellationTokenSource _TaskCancelTokenSource = new CancellationTokenSource();
        internal static event EventHandler<BufferOrderState> OnBufferOrderStarted;

        public bool TaskCancelledFlag { get; private set; } = false;
        public bool TaskAbortedFlag { get; private set; } = false;

        public ALARMS AlarmWhenTaskAborted;

        public bool TrafficControlling { get; private set; } = false;
        public string TaskCancelReason { get; private set; } = "";
        public string TaskAbortReason { get; private set; } = "";
        public IAGV Agv { get; protected set; }

        public event EventHandler OnLoadingAtTransferStationTaskFinish;

        public event EventHandler<OrderHandlerBase> OnTaskCanceled;
        public event EventHandler<OrderHandlerBase> OnOrderFinish;

        protected Logger logger;
        public OrderHandlerBase()
        {
            logger = LogManager.GetLogger("OrderHandle");
        }
        public OrderHandlerBase(AGVSDbContext agvsDb, SemaphoreSlim taskTbModifyLock) : base(agvsDb, taskTbModifyLock)
        {
            logger = LogManager.GetLogger("OrderHandle");
        }


        public virtual async Task StartOrder(IAGV Agv)
        {
            this.Agv = Agv;
            _ = Task.Run(async () =>
            {

                //TODO 如果取放貨的起終點為 buffer 類，需將其趕至可停車點停車
                (bool isSourceBuffer, bool isDestineBuffer) = IsWorkStationContainBuffer(out MapPoint sourcePt, out MapPoint destinePt);

                if (isSourceBuffer || isDestineBuffer)
                {
                    BufferOrderState state = new()
                    {
                        orderBase = this,
                        bufferFrom = isSourceBuffer ? sourcePt : null,
                        bufferTo = isDestineBuffer ? destinePt : null
                    };
                    OnBufferOrderStarted?.Invoke(this, state);

                    if (state.returnCode != ALARMS.NONE)
                    {
                        _SetOrderAsFaiiureState(state.message, state.returnCode);
                        return;
                    }
                }

                //
                await SyncTrafficStateFromAGVSystem();
                double beginMileageOfVehicle = Agv.states.Odometry;
                _SetOrderAsRunningState();
                try
                {
                    while (SequenceTaskQueue.Count > 0)
                    {
                        await Task.Delay(100);
                        Agv.TaskExecuter.Init();
                        Agv.NavigationState.StateReset();
                        _CurrnetTaskFinishResetEvent.Reset();
                        TaskBase task = SequenceTaskQueue.Dequeue();

                        task.TaskName = OrderData.TaskName;
                        task.TaskSequence = CompleteTaskStack.Count + 1;
                        RunningTask = task;
                        task.OnTaskDownloadToAGVButAGVRejected += (_alarm) =>
                        {
                            AbortOrder(_alarm);
                        };
                        logger.Info($"[{Agv.Name}] Task-{task.ActionType} 開始");
                        var dispatch_result = await task.DistpatchToAGV();
                        if (!dispatch_result.confirmed)
                        {
                            throw new VMSException(dispatch_result.message)
                            {
                                Alarm_Code = dispatch_result.alarm_code == ALARMS.NONE ? ALARMS.SYSTEM_ERROR : dispatch_result.alarm_code
                            };
                            //AlarmManagerCenter.AddAlarmAsync(dispatch_result.alarm_code, level: ALARM_LEVEL.ALARM, Equipment_Name: this.Agv.Name, location: this.Agv.currentMapPoint.Graph.Display, taskName: this.RunningTask.TaskName);
                        }

                        task.Dispose();
                        (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) taskchange = task.ActionFinishInvoke();
                        if (taskchange.continuetask == false)
                        {
                            _SetOrderAsFaiiureState(taskchange.errorMsg, taskchange.alarmCode);
                            return;
                        }
                        if (taskchange.task != null)
                            ModifyOrder(taskchange.task);

                        logger.Info($"[{Agv.Name}] Task-{task.ActionType} 結束");

                        bool _isTaskFail = await DetermineTaskState();
                        if (_isTaskFail)
                        {
                            return;
                        }
                        CompleteTaskStack.Push(task);


                    }

                    _SetOrderAsFinishState();
                    if (OrderData.need_change_agv && this.RunningTask.Stage == VehicleMovementStage.LoadingAtTransferStation)
                    {
                        Console.WriteLine("轉送任務-[來源->轉運站任務] 結束");
                        LoadingAtTransferStationTaskFinishInvoke();
                    }
                }
                catch (VMSException ex)
                {
                    _HandleVMSException(ex);
                    return;
                }
                catch (VMSExceptionAbstract ex)
                {
                    _HandleVMSException(ex);
                    return;
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                    _SetOrderAsFaiiureState(ex.Message, ALARMS.SYSTEM_ERROR);
                    ActionsWhenOrderCancle();
                    RunningTask.Dispose();
                    return;
                }
                finally
                {
                    Agv.NavigationState.StateReset();
                    Agv.NavigationState.ResetNavigationPoints();
                    Agv.taskDispatchModule.OrderHandler.RunningTask = new MoveToDestineTask();
                    ActionsWhenOrderCancle();
                    //Agv.taskDispatchModule.AsyncTaskQueueFromDatabase();

                    double finalMileageOfVehicle = Agv.states.Odometry;
                    OrderData.TotalMileage = finalMileageOfVehicle - beginMileageOfVehicle;
                    ModifyOrder(OrderData);
                    DisposeActionOfCompleteTasks();
                }

                async Task<bool> DetermineTaskState()
                {
                    bool isTaskFail = false;
                    while (Agv.main_state == MAIN_STATUS.RUN)
                    {
                        await Task.Delay(100);
                    }
                    isTaskFail = false;
                    bool isAGVStatusDown = Agv.main_state == MAIN_STATUS.DOWN;
                    if (TaskAbortedFlag || isAGVStatusDown)
                    {
                        TaskCancelledFlag = false;
                        logger.Warn($"Task Aborted!.{TaskCancelReason}");
                        _SetOrderAsFaiiureState(TaskAbortReason, TaskAbortedFlag ? AlarmWhenTaskAborted : ALARMS.AGV_STATUS_DOWN);
                        ActionsWhenOrderCancle();
                        isTaskFail = true;
                        await AbortOrder(Agv.states.Alarm_Code);
                        return isTaskFail;
                    }
                    if (TaskCancelledFlag)
                    {
                        logger.Warn($"Task canceled.{TaskCancelReason}");
                        _SetOrderAsCancelState(TaskCancelReason);
                        ActionsWhenOrderCancle();
                        isTaskFail = true;
                    }
                    return isTaskFail;
                }

                async void _HandleVMSException(VMSExceptionAbstract ex)
                {
                    await Task.Delay(1000);
                    logger.Error(ex);
                    bool _isAgvDown = Agv.main_state == MAIN_STATUS.DOWN;
                    if (!_isAgvDown && ex.Alarm_Code == ALARMS.Task_Canceled)
                        _SetOrderAsCancelState(TaskCancelReason);
                    else
                        _SetOrderAsFaiiureState(ex.Message, _isAgvDown ? ALARMS.AGV_STATUS_DOWN : ex.Alarm_Code);
                    ActionsWhenOrderCancle();
                    RunningTask.Dispose();
                }
            });
        }

        private void DisposeActionOfCompleteTasks()
        {
            CompleteTaskStack.Where(tk => tk.OrderTransfer != null && tk.OrderTransfer.State == OrderTransfer.STATES.BETTER_VEHICLE_SEARCHING)
                             .Select(tk => tk.OrderTransfer.OrderDone());

            CompleteTaskStack.Clear();
        }

        public (bool isSourceBuffer, bool isDestineBuffer) IsWorkStationContainBuffer(out MapPoint sourcePt, out MapPoint destinePt)
        {
            sourcePt = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
            destinePt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);

            bool isSourceBuffer = sourcePt != null && _isBufferStation(sourcePt);
            bool isDestineBuffer = destinePt != null && _isBufferStation(destinePt);

            return (isSourceBuffer, isDestineBuffer);

            bool _isBufferStation(MapPoint mapPoint)
            {
                var _stationType = mapPoint.StationType;
                return _stationType == MapPoint.STATION_TYPE.Buffer || _stationType == MapPoint.STATION_TYPE.Buffer_EQ || _stationType == MapPoint.STATION_TYPE.Charge_Buffer;
            }

        }

        protected async Task SyncTrafficStateFromAGVSystem()
        {
            logger.Trace($"DispatchCenter.SyncTrafficStateFromAGVSystemInvoke Invoke");
            await DispatchCenter.SyncTrafficStateFromAGVSystemInvoke();
        }

        private SemaphoreSlim _HandleTaskStateFeedbackSemaphoreSlim = new SemaphoreSlim(1, 1);

        public void LoadingAtTransferStationTaskFinishInvoke()
        {
            OnLoadingAtTransferStationTaskFinish?.Invoke(this, EventArgs.Empty);
        }
        internal async void HandleAGVFeedbackAsync(FeedbackData feedbackData)
        {
            await _HandleTaskStateFeedbackSemaphoreSlim.WaitAsync();
            try
            {
                logger.Info($"{Agv.Name} 任務回報 => {feedbackData.ToJson()}");
                _ = Task.Run(async () =>
                {
                    if (feedbackData.TaskStatus == TASK_RUN_STATUS.ACTION_FINISH)
                    {
                        await HandleAGVActionFinishFeedback();
                    }
                    else if (feedbackData.TaskStatus == TASK_RUN_STATUS.NAVIGATING)
                    {
                        HandleAGVNavigatingFeedback(feedbackData);
                    }
                    else if (feedbackData.TaskStatus == TASK_RUN_STATUS.ACTION_START)
                        HandleAGVActionStartFeedback();
                });
            }
            catch (Exception ex)
            {
                _SetOrderAsFaiiureState(ex.Message, ALARMS.SYSTEM_ERROR);
            }
            finally
            {
                _HandleTaskStateFeedbackSemaphoreSlim.Release();
            }

        }

        protected virtual void HandleAGVActionStartFeedback()
        {
            var destineTag = RunningTask.DestineTag;

            if (RunningTask.ActionType != ACTION_TYPE.None)
            {
                MapPoint destineMapPoint = StaMap.GetPointByTagNumber(destineTag);
                StaMap.RegistPoint(Agv.Name, destineMapPoint, out var _);
            }
        }

        protected virtual void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {

            RunningTask.HandleAGVNavigatingFeedback(feedbackData);
        }

        protected virtual async Task HandleAGVActionFinishFeedback()
        {
            MAIN_STATUS GetAgvMainState()
            {
                return Agv.main_state;
            }

            if (TrafficControlling)
                return;

            while (GetAgvMainState() == MAIN_STATUS.RUN)
            {
                await Task.Delay(10);
            }

            MAIN_STATUS _state_when_action_finish = GetAgvMainState();
            if (RunningTask.IsAGVReachDestine && (_state_when_action_finish == MAIN_STATUS.IDLE || _state_when_action_finish == MAIN_STATUS.Charging))
            {
                // RunningTask.ActionFinishInvoke();
                _CurrnetTaskFinishResetEvent.Set();
            }
            else if (_state_when_action_finish == MAIN_STATUS.DOWN)
                AbortOrder(Agv.states.Alarm_Code);


        }


        internal async Task AbortOrder(ALARMS agvsAlarm)
        {
            AlarmWhenTaskAborted = agvsAlarm;
            TaskAbortedFlag = true;
            RunningTask.CancelTask();
            _CurrnetTaskFinishResetEvent.Set();
        }
        internal async Task AbortOrder(AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode[] alarm_Code)
        {
            TaskAbortedFlag = true;
            TaskAbortReason = string.Join(",", alarm_Code.Where(alarm => alarm.Alarm_Category != 0).Select(alarm => alarm.FullDescription));
            RunningTask.CancelTask();
            _CurrnetTaskFinishResetEvent.Set();
        }
        internal async Task<(bool confirmed, string message)> CancelOrder(string taskName, string reason = "")
        {
            if (this.OrderData.TaskName != taskName)
            {
                return (false, "Task Name Not Match");
            }

            TaskCancelledFlag = true;
            TaskCancelReason = reason;
            RunningTask.CancelTask();
            _CurrnetTaskFinishResetEvent.Set();
            return (true, "");
        }
        private void _SetOrderAsRunningState()
        {
            OrderData.StartTime = DateTime.Now;
            OrderData.State = TASK_RUN_STATUS.NAVIGATING;
            OrderData.StartLocationTag = Agv.currentMapPoint.TagNumber;
            ModifyOrder(OrderData);
        }

        private async Task _SetOrderAsCancelState(string taskCancelReason)
        {
            RunningTask.CancelTask();
            UnRegistPoints();
            OrderData.State = TASK_RUN_STATUS.CANCEL;
            OrderData.FinishTime = DateTime.Now;
            OrderData.FailureReason = TaskCancelReason;
            if (string.IsNullOrEmpty(OrderData.FailureReason))
            {

            }
            (bool confirm, string message) v = await AGVSSerivces.TaskReporter((OrderData, MCSCIMService.TaskStatus.cancel));
            if (v.confirm == false)
                LOG.WARN($"{v.message}");

            await Task.Delay(300);

            bool isTaskCanceled = DatabaseCaches.TaskCaches.CompleteTasks.Any(tk => tk.TaskName == OrderData.TaskName && tk.State == TASK_RUN_STATUS.CANCEL);
            bool isTaskReAssigned = DatabaseCaches.TaskCaches.InCompletedTasks.Any(tk => tk.TaskName == OrderData.TaskName && tk.DesignatedAGVName != Agv.Name);
            bool isOrderAlreadyTransferToOtherVehicle = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv).Any(v => v.taskDispatchModule.taskList.Any(tk => tk.TaskName == OrderData.TaskName));
            if (isTaskCanceled || isTaskReAssigned || isOrderAlreadyTransferToOtherVehicle)
            {
                return;
            }

            ModifyOrder(OrderData);
            OnTaskCanceled?.Invoke(this, this);
        }
        protected virtual void _SetOrderAsFinishState()
        {
            UnRegistPoints();
            OrderData.State = TASK_RUN_STATUS.ACTION_FINISH;
            OrderData.FinishTime = DateTime.Now;
            ModifyOrder(OrderData);
            OnOrderFinish?.Invoke(this, this);
            if (PartsAGVSHelper.NeedRegistRequestToParts)
            {
                PartsAGVSHelper.UnRegistStationExceptSpeficStationName(new List<string>()
                {
                     Agv.currentMapPoint.Graph.Display
                });
                PartsAGVSHelper.RegistStationRequestToAGVS(new List<string>()
                {
                    Agv.currentMapPoint.Graph.Display
                });
            }

        }

        protected async void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm)
        {
            try
            {
                clsAlarmDto alarmDto = new clsAlarmDto();
                try
                {
                    if (alarm != ALARMS.AGV_STATUS_DOWN)
                    {
                        alarmDto = await AlarmManagerCenter.AddAlarmAsync(alarm, level: ALARM_LEVEL.ALARM, location: Agv?.currentMapPoint.Graph.Display, Equipment_Name: Agv?.Name, taskName: OrderData.TaskName);
                    }
                }
                catch (Exception ex)
                {
                    logger.Fatal(ex);
                }

                RunningTask.CancelTask();
                UnRegistPoints();
                OrderData.State = TASK_RUN_STATUS.FAILURE;
                OrderData.FinishTime = DateTime.Now;
                OrderData.FailureReason = alarmDto.AlarmCode == 0 ? FailReason : $"[{alarmDto.AlarmCode}] {alarmDto.Description})";

                if (Agv != null && (alarm == ALARMS.AGV_STATUS_DOWN || Agv?.main_state == MAIN_STATUS.DOWN))
                {
                    var agvAlarmsDescription = string.Join(",", Agv.states.Alarm_Code.Where(alarm => alarm.Alarm_Category != 0).Select(alarm => alarm.FullDescription));
                    OrderData.FailureReason = agvAlarmsDescription;
                }

                ModifyOrder(OrderData);
                (bool confirm, string message) v = await AGVSSerivces.TaskReporter((OrderData, MCSCIMService.TaskStatus.fail));
                if (v.confirm == false)
                    LOG.WARN($"{v.message}");


                if (OrderData.Action == ACTION_TYPE.Carry)
                {
                    MapPoint sourceEQPt = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
                    MapPoint destineEQPt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                    var orderFailureNotify = new
                    {
                        classify = "carry-order-failure",
                        message_Zh = $"搬運任務 [{sourceEQPt.Graph.Display}]->[{destineEQPt.Graph.Display}] 失敗:{OrderData.FailureReason}",
                        message_En = $"Carry Order From [{sourceEQPt.Graph.Display}] To [{destineEQPt.Graph.Display}] Failure:{OrderData.FailureReason}",
                    };
                    NotifyServiceHelper.ERROR(orderFailureNotify.ToJson(Newtonsoft.Json.Formatting.None));
                }
            }
            catch (Exception ex)
            {
                logger.Fatal(ex);
            }
        }

        internal virtual List<int> GetNavPathTags()
        {
            if (Agv == null)
                return new List<int>();
            //return GetNavPathTags(RunningTask.TaskDonwloadToAGV.ExecutingTrajecory.Select(p => p.Point_ID));
            return GetNavPathTags(RunningTask.MoveTaskEvent.AGVRequestState.RemainTagList);
        }

        protected List<int> GetNavPathTags(IEnumerable<int> AllTagsOfPath)
        {
            int _agv_current_tag = Agv.states.Last_Visited_Node;
            int indexOfAgvCurrentTag = AllTagsOfPath.ToList().FindIndex(tag => tag == _agv_current_tag);
            return AllTagsOfPath.Skip(indexOfAgvCurrentTag).ToList();
        }

        protected virtual async Task ActionsWhenOrderCancle()
        {
            //Agv.taskDispatchModule.AsyncTaskQueueFromDatabase();
            if (PartsAGVSHelper.NeedRegistRequestToParts)
            {
                try
                {
                    var excutingTraj = RunningTask.TaskDonwloadToAGV.ExecutingTrajecory;
                    if (!excutingTraj.Any())
                        return;
                    var lastVisiPointIndex = excutingTraj.ToList().FindIndex(pt => pt.Point_ID == Agv.currentMapPoint.TagNumber);
                    if (lastVisiPointIndex == -1)
                        return;
                    var stopPtTag = 0;
                    if (lastVisiPointIndex == excutingTraj.Count() - 1)
                        stopPtTag = excutingTraj.Last().Point_ID;
                    else
                        stopPtTag = excutingTraj[lastVisiPointIndex + 1].Point_ID;
                    var expectRegionName = StaMap.GetPointByTagNumber(stopPtTag).Graph.Display;
                    await PartsAGVSHelper.UnRegistStationExceptSpeficStationName(new List<string>() { expectRegionName });
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                }
            }
        }
        private void UnRegistPoints()
        {
            if (Agv == null)
                return;
            StaMap.UnRegistPointsOfAGVRegisted(this.Agv);
            Agv.NavigationState.ResetNavigationPoints();
        }
        internal void StartTrafficControl()
        {
            TrafficControlling = true;
            RunningTask.MoveTaskEvent.TrafficResponse.ConfirmResult = clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.CANCEL;
            RunningTask.CancelTask();
        }

        internal void FinishTrafficControl()
        {
            TrafficControlling = false;
            RunningTask.MoveTaskEvent.TrafficResponse.ConfirmResult = clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.ACCEPTED_GOTO_NEXT_GOAL;
        }
    }
}

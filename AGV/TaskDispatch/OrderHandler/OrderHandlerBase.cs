using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Notify;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;
using Newtonsoft.Json;
using NLog;
using Polly;
using Polly.Retry;
using System.Diagnostics;
using System.Threading.Tasks;
using VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch;
using VMSystem.Extensions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    /// <summary>
    /// 處理訂單任務
    /// </summary>
    public abstract partial class OrderHandlerBase : clsTaskDatabaseWriteableAbstract
    {
        public class BufferOrderState
        {
            public OrderHandlerBase orderBase { get; set; }

            public MapPoint bufferFrom { get; set; }

            public MapPoint bufferTo { get; set; }

            public string message { get; set; } = string.Empty;

            public ALARMS returnCode { get; set; } = ALARMS.NONE;

            public CancellationToken cancellationToken { get; set; } = new CancellationToken();

        }


        public abstract ACTION_TYPE OrderAction { get; }
        public Queue<TaskBase> SequenceTaskQueue { get; set; } = new Queue<TaskBase>();
        public Stack<TaskBase> CompleteTaskStack { get; set; } = new Stack<TaskBase>();
        public clsTaskDto OrderData { get; internal set; } = new clsTaskDto();
        public TaskBase RunningTask { get; internal set; } = new MoveToDestineTask();
        protected ManualResetEvent _CurrnetTaskFinishResetEvent = new ManualResetEvent(false);
        private CancellationTokenSource _TaskCancelTokenSource = new CancellationTokenSource();
        private CancellationTokenSource _WaitOtherVehicleLeaveFromPortCancelTokenSource = new CancellationTokenSource();
        private bool _UserAortOrderFlag = false;
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

        internal static event EventHandler<OrderStartEvnetArgs> OnOrderStart;

        private SECSConfigsService secsConfigsService = new SECSConfigsService();

        protected Logger logger;

        private List<int> TagsTracking = new List<int>();

        public OrderHandlerBase()
        {
            logger = LogManager.GetLogger("OrderHandle");
        }
        public OrderHandlerBase(SemaphoreSlim taskTbModifyLock) : base(taskTbModifyLock)
        {
            logger = LogManager.GetLogger("OrderHandle");
        }


        public virtual async Task StartOrder(IAGV Agv)
        {
            this.Agv = Agv;
            TagsTracking = new List<int>() { Agv.currentMapPoint.TagNumber };
            Agv.OnMapPointChanged += HandleAGVMapPointChanged;
            OrderStartEvnetArgs _OrderStartEventArgs = new OrderStartEvnetArgs(this);
            OnOrderStart?.Invoke(this, _OrderStartEventArgs);
            secsConfigsService = _OrderStartEventArgs.secsConfigsService;

            logger.Trace($"OrderHandlerBase.StartOrder Invoke. Transfer Completed Result Codes=\r\n {secsConfigsService.transferReportConfiguration.ResultCodes.ToJson()}");

            TrajectoryRecorder trajectoryRecorder = new TrajectoryRecorder(Agv, OrderData);
            trajectoryRecorder.Start();



            _ = Task.Run(async () =>
            {
                await SetOrderProgress(VehicleMovementStage.Not_Start_Yet);

                if (OrderData.isVehicleAssignedChanged)
                {
                    NotifyServiceHelper.INFO($"{this.Agv.Name} 接收任務轉移-開始執行任務");
                    OrderData.isVehicleAssignedChanged = false;
                    await ModifyOrder(OrderData);
                }
                //TODO 如果取放貨的起終點為 buffer 類，需將其趕至可停車點停車
                (bool isSourceBuffer, bool isDestineBuffer) = IsWorkStationContainBuffer(out MapPoint sourcePt, out MapPoint destinePt);

                if (isSourceBuffer || isDestineBuffer)
                {
                    _WaitOtherVehicleLeaveFromPortCancelTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(Debugger.IsAttached ? 10 : 300));
                    BufferOrderState state = new()
                    {
                        orderBase = this,
                        bufferFrom = isSourceBuffer ? sourcePt : null,
                        bufferTo = isDestineBuffer ? destinePt : null,
                        cancellationToken = _WaitOtherVehicleLeaveFromPortCancelTokenSource.Token
                    };

                    await SetOrderProgress(VehicleMovementStage.WaitingOtherVehicleLeaveAwayPort);
                    OnBufferOrderStarted?.Invoke(this, state);
                    if (state.returnCode != ALARMS.NONE)
                    {
                        ALARMS alarmCode = _UserAortOrderFlag ? ALARMS.TrafficDriveVehicleAwayTaskCanceledByManualWhenWaitingVehicleLeave : state.returnCode;
                        _SetOrderAsFaiiureState(state.message, alarmCode);
                        //因為根本沒有開始訂單要直接上報TransferCompleted,
                        this.transportCommand.ResultCode = 1;
                        if (this.OrderAction == ACTION_TYPE.Carry)
                            MCSCIMService.TransferCompletedReport(this.transportCommand);
                        _SetOrderAsFaiiureState("", alarmCode);
                        return;
                    }
                }
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
                        task.orderHandler = this;
                        task.TaskName = OrderData.TaskName;
                        task.TaskSequence = CompleteTaskStack.Count + 1;
                        RunningTask = task;

                        task.OnTaskDownloadToAGVButAGVRejected += (_alarm) =>
                        {
                            AbortOrder(_alarm);
                        };
                        logger.Info($"[{Agv.Name}] Task-{task.ActionType} 開始");

                        await SetOrderProgress(task.Stage);

                        var dispatch_result = await task.DistpatchToAGV();
                        if (!dispatch_result.confirmed)
                        {
                            if (dispatch_result.alarm_code == ALARMS.AGV_STATUS_DOWN)
                                throw new AGVStatusDownException();

                            throw new VMSException(dispatch_result.message)
                            {
                                Alarm_Code = dispatch_result.alarm_code == ALARMS.NONE ? ALARMS.SYSTEM_ERROR : dispatch_result.alarm_code
                            };
                        }

                        task.Dispose();
                        (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) taskchange = await task.ActionFinishInvoke();
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
                catch (AGVStatusDownException ex)
                {
                    _HandleVMSException(ex);
                    return;
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
                    trajectoryRecorder.Stop();

                    Task.Delay(1000).ContinueWith(async t => await MCSCIMService.VehicleUnassignedReport(Agv.AgvIDStr, OrderData.TaskName));
                    await ResetVehicleRegistedPoints();
                    Agv.taskDispatchModule.OrderHandler.RunningTask = new MoveToDestineTask();
                    ActionsWhenOrderCancle();
                    //Agv.taskDispatchModule.AsyncTaskQueueFromDatabase();
                    double finalMileageOfVehicle = Agv.states.Odometry;
                    OrderData.TotalMileage += (finalMileageOfVehicle - beginMileageOfVehicle);
                    Agv.OnMapPointChanged -= HandleAGVMapPointChanged;
                    OrderData.TagsTracking = string.Join("-", TagsTracking);
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
                        _SetOrderAsCancelState(TaskCancelReason, ALARMS.Task_Canceled);
                        ActionsWhenOrderCancle();
                        isTaskFail = true;
                    }
                    return isTaskFail;
                }

                async void _HandleVMSException(VMSExceptionAbstract ex)
                {
                    logger.Error(ex);

                    bool _isAgvDown = ex.Alarm_Code == ALARMS.AGV_STATUS_DOWN || Agv.main_state == MAIN_STATUS.DOWN;
                    if (!_isAgvDown && ex.Alarm_Code == ALARMS.Task_Canceled)
                        _SetOrderAsCancelState(TaskCancelReason, ex.Alarm_Code);
                    else
                        _SetOrderAsFaiiureState(ex.Message, _isAgvDown ? ALARMS.AGV_STATUS_DOWN : ex.Alarm_Code);
                    ActionsWhenOrderCancle();
                    RunningTask.Dispose();
                }


            });
        }

        private void HandleAGVMapPointChanged(object? sender, int tag)
        {
            int lastTag = TagsTracking.LastOrDefault();
            if (lastTag != tag)
                TagsTracking.Add(tag);
        }

        public virtual void BuildTransportCommandDto()
        {
            var sourceStationType = OrderData.From_Station_Tag.GetMapPoint().StationType;
            this.transportCommand = new MCSCIMService.TransportCommandDto()
            {
                CommandID = OrderData.TaskName,
                CarrierID = OrderData.Carrier_ID,
                CarrierLoc = OrderData.soucePortID,
                CarrierZoneName = OrderData.sourceZoneID,
                Dest = OrderData.destinePortID,
                ResultCode = 0,
            };
        }

        protected virtual async Task ResetVehicleRegistedPoints()
        {
            //bool isAGVDownAtRackPort = Agv.main_state == MAIN_STATUS.DOWN && Agv.IsVehicleAtBuffer();
            //if (isAGVDownAtRackPort)
            //{
            //    StaMap.RegistPoint(Agv.Name, Agv.currentMapPoint.TargetNormalPoints(), out string mesg);
            //    return;
            //}
            await UnRegistPoints();
            Agv.NavigationState.StateReset(this.OrderAction == ACTION_TYPE.Charge || OrderAction == ACTION_TYPE.Park ? VehicleNavigationState.WORKSTATION_MOVE_STATE.FORWARDING : VehicleNavigationState.WORKSTATION_MOVE_STATE.BACKWARDING);
            Agv.NavigationState.ResetNavigationPoints();
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
        internal async Task<(bool confirmed, string message)> CancelOrder(string taskName, bool isManual, string reason = "", string hostAction = null)
        {
            if (this.OrderData.TaskName != taskName)
            {
                return (false, "Task Name Not Match");
            }

            TaskCancelledFlag = true;
            TaskCancelReason = reason;
            RunningTask.CancelTask(hostAction);
            _CurrnetTaskFinishResetEvent.Set();
            _WaitOtherVehicleLeaveFromPortCancelTokenSource?.Cancel();
            _UserAortOrderFlag = isManual;
            return (true, "");
        }
        private void _SetOrderAsRunningState()
        {
            OrderData.StartTime = DateTime.Now;
            OrderData.State = TASK_RUN_STATUS.NAVIGATING;
            OrderData.StartLocationTag = Agv.currentMapPoint.TagNumber;
            ModifyOrder(OrderData);
        }

        protected virtual async Task _SetOrderAsCancelState(string taskCancelReason, ALARMS alarmCode)
        {
            RunningTask.CancelTask();
            OrderData.State = TASK_RUN_STATUS.CANCEL;
            OrderData.FinishTime = DateTime.Now;
            OrderData.FailureReason = TaskCancelReason;
            if (string.IsNullOrEmpty(OrderData.FailureReason))
            {

            }
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
            OrderData.State = TASK_RUN_STATUS.ACTION_FINISH;
            OrderData.FinishTime = DateTime.Now;
            OrderData.currentProgress = VehicleMovementStage.Completed;
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

        protected virtual async void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm)
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
                OrderData.State = TASK_RUN_STATUS.FAILURE;
                OrderData.FinishTime = DateTime.Now;
                if (Agv != null && (alarm == ALARMS.AGV_STATUS_DOWN || Agv?.main_state == MAIN_STATUS.DOWN))
                    OrderData.FailureReason = CreateFailReasonOfAGVDown();
                else
                    OrderData.FailureReason = alarmDto.AlarmCode == 0 ? FailReason : $"[{alarmDto.AlarmCode}] {alarmDto.Description})";

                ModifyOrder(OrderData);

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

        private string CreateFailReasonOfAGVDown()
        {
            // 定义重试策略
            RetryPolicy<string> retryPolicy = Policy<string>.HandleResult(result => string.IsNullOrEmpty(result))
                                                            .WaitAndRetry(3, retryAttempt => TimeSpan.FromMilliseconds(500),
                                                                (result, timeSpan, retryCount, context) =>
                                                                {
                                                                    logger.Warn($"嘗試同步車輛({Agv.Name})的即時異常碼失敗，重試次數：{retryCount}，等待時間：{timeSpan}");
                                                                });
            string agvAlarmsDescription = retryPolicy.Execute(() =>
            {
                return string.Join(",", Agv.states.Alarm_Code.Where(alarm => alarm.Alarm_Category != 0).Select(alarm => alarm.FullDescription));
            });

            if (string.IsNullOrEmpty(agvAlarmsDescription))
                if (AlarmManagerCenter.AlarmCodes.TryGetValue(ALARMS.AGV_STATUS_DOWN, out var alarmDto))
                    return alarmDto.Description;
                else
                    return "AGV 狀態異常(AGV Status Down)";
            else
                return agvAlarmsDescription;
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
        private async Task UnRegistPoints()
        {
            if (Agv == null)
                return;
            while (!await StaMap.UnRegistPointsOfAGVRegisted(this.Agv))
            {
                await Task.Delay(200);
            }
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
        async Task SetOrderProgress(VehicleMovementStage progress)
        {
            try
            {
                OrderData.currentProgress = progress;
                await ModifyOrder(OrderData);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }


        internal class OrderStartEvnetArgs : EventArgs
        {
            internal SECSConfigsService secsConfigsService = new SECSConfigsService();
            internal readonly OrderHandlerBase OrderHandler;
            internal bool isSecsConfigServiceInitialized = false;
            internal OrderStartEvnetArgs(OrderHandlerBase OrderHandler)
            {
                this.OrderHandler = OrderHandler;
            }
        }

    }
}

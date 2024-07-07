using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;
using NLog;
using System.Threading.Tasks;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    /// <summary>
    /// 處理訂單任務
    /// </summary>
    public abstract class OrderHandlerBase : clsTaskDatabaseWriteableAbstract
    {
        public abstract ACTION_TYPE OrderAction { get; }
        public Queue<TaskBase> SequenceTaskQueue { get; set; } = new Queue<TaskBase>();
        public Stack<TaskBase> CompleteTaskStack { get; set; } = new Stack<TaskBase>();
        public clsTaskDto OrderData { get; internal set; } = new clsTaskDto();
        public TaskBase RunningTask { get; internal set; } = new MoveToDestineTask();
        protected ManualResetEvent _CurrnetTaskFinishResetEvent = new ManualResetEvent(false);
        private CancellationTokenSource _TaskCancelTokenSource = new CancellationTokenSource();


        public bool TaskCancelledFlag { get; private set; } = false;
        public bool TaskAbortedFlag { get; private set; } = false;

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

        public virtual async Task StartOrder(IAGV Agv)
        {
            _ = Task.Run(async () =>
            {

                this.Agv = Agv;
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
                            if (dispatch_result.alarm_code == ALARMS.Task_Canceled)
                            {
                                TaskCancelledFlag = true;
                                bool isTaskFail = await DetermineTaskState();
                                if (isTaskFail)
                                {
                                    return;
                                }
                                return;
                            }
                            AlarmManagerCenter.AddAlarmAsync(dispatch_result.alarm_code, level: ALARM_LEVEL.ALARM, Equipment_Name: this.Agv.Name, location: this.Agv.currentMapPoint.Graph.Display, taskName: this.RunningTask.TaskName);
                            throw new Exception(dispatch_result.alarm_code.ToString());
                        }

                        task.Dispose();
                        task.ActionFinishInvoke();

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
                    //Agv.taskDispatchModule.AsyncTaskQueueFromDatabase();
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
                        _SetOrderAsFaiiureState(TaskAbortReason, TaskAbortedFlag ? ALARMS.Task_Aborted : ALARMS.AGV_STATUS_DOWN);
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

            });
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
                logger.Info($"{RunningTask.Agv.Name} 任務回報 => {feedbackData.ToJson()}");
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
                RunningTask.ActionFinishInvoke();
                _CurrnetTaskFinishResetEvent.Set();
            }
            else if (_state_when_action_finish == MAIN_STATUS.DOWN)
                AbortOrder(Agv.states.Alarm_Code);


        }


        internal async Task AbortOrder(ALARMS agvsAlarm)
        {
            AGVSystemCommonNet6.Alarm.clsAlarmDto _alarmDto = await AlarmManagerCenter.AddAlarmAsync(agvsAlarm);
            TaskAbortedFlag = true;
            TaskAbortReason = _alarmDto.Description;
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
            RaiseTaskDtoChange(this, OrderData);
        }

        private void _SetOrderAsCancelState(string taskCancelReason)
        {
            RunningTask.CancelTask();
            UnRegistPoints();
            OrderData.State = TASK_RUN_STATUS.CANCEL;
            OrderData.FinishTime = DateTime.Now;
            OrderData.FailureReason = TaskCancelReason;
            RaiseTaskDtoChange(this, OrderData);
            OnTaskCanceled?.Invoke(this, this);
        }
        protected virtual void _SetOrderAsFinishState()
        {
            UnRegistPoints();
            OrderData.State = TASK_RUN_STATUS.ACTION_FINISH;
            OrderData.FinishTime = DateTime.Now;
            RaiseTaskDtoChange(this, OrderData);
            OnOrderFinish?.Invoke(this, this);
            if (PartsAGVSHelper.NeedRegistRequestToParts)
            {
                PartsAGVSHelper.UnRegistStationExceptSpeficStationName(new List<string>()
                {
                     Agv.currentMapPoint.Graph.Display
                });
            }

        }

        protected async void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm)
        {
            RunningTask.CancelTask();
            UnRegistPoints();
            OrderData.State = TASK_RUN_STATUS.FAILURE;
            OrderData.FinishTime = DateTime.Now;
            OrderData.FailureReason = FailReason;
            RaiseTaskDtoChange(this, OrderData);
            MCSCIMService.TaskReporter((OrderData, 7));
            AlarmManagerCenter.AddAlarmAsync(alarm, level: ALARM_LEVEL.ALARM, taskName: OrderData.TaskName);
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

        protected virtual void ActionsWhenOrderCancle()
        {
            //Agv.taskDispatchModule.AsyncTaskQueueFromDatabase();
            if (PartsAGVSHelper.NeedRegistRequestToParts)
            {
                //0,1,2,3
                var excutingTraj = RunningTask.TaskDonwloadToAGV.ExecutingTrajecory;
                var lastVisiPointIndex = excutingTraj.ToList().FindIndex(pt => pt.Point_ID == Agv.currentMapPoint.TagNumber);
                var stopPtTag = 0;
                if (lastVisiPointIndex == excutingTraj.Count() - 1)
                    stopPtTag = excutingTraj.Last().Point_ID;
                else
                    stopPtTag = excutingTraj[lastVisiPointIndex + 1].Point_ID;
                var expectRegionName = StaMap.GetPointByTagNumber(stopPtTag).Graph.Display;
                PartsAGVSHelper.UnRegistStationExceptSpeficStationName(new List<string>()
                {
                    expectRegionName
                });
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

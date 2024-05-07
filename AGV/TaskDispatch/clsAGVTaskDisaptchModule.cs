using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.MAP;
using System.Collections.Generic;
using System.Diagnostics;
using static AGVSystemCommonNet6.AGVDispatch.clsTaskDto;
using static VMSystem.TrafficControl.TrafficControlCenter;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Exceptions;
using RosSharp.RosBridgeClient.MessageTypes.Sensor;
using System.Threading.Tasks;
using VMSystem.VMS;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.Win32;
using AGVSystemCommonNet6.AGVDispatch.Model;
using System.Timers;
using AGVSystemCommonNet6.DATABASE.Helpers;
using static AGVSystemCommonNet6.MAP.PathFinder;
using VMSystem.AGV.TaskDispatch;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using AGVSystemCommonNet6.AGVDispatch.RunMode;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ObjectiveC;
using AGVSystemCommonNet6.AGVDispatch;
using System.Drawing;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using static VMSystem.AGV.TaskDispatch.IAGVTaskDispather;
using VMSystem.TrafficControl.Solvers;
using VMSystem.AGV.TaskDispatch.Tasks;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    /// <summary>
    /// 任務派送模組
    /// </summary>
    public partial class clsAGVTaskDisaptchModule : IAGVTaskDispather
    {
        public enum AGV_ORDERABLE_STATUS
        {
            EXECUTABLE,
            EXECUTING,
            AGV_STATUS_ERROR,
            NO_ORDER,
            AGV_OFFLINE,
            EXECUTING_RESUME,
            BatteryLowLevel,
            ChargingButBatteryUnderMiddleLevel,
            BatteryStatusError,
            SystemError
        }

        public IAGV agv;
        private PathFinder pathFinder = new PathFinder();
        private DateTime LastNonNoOrderTime;
        private bool _IsChargeTaskCreating;
        private bool _IsChargeStatesChecking = false;

        private AGV_ORDERABLE_STATUS previous_OrderExecuteState = AGV_ORDERABLE_STATUS.NO_ORDER;
        private bool _IsChargeTaskNotExcutableCauseCargoExist = false;
        private bool IsChargeTaskNotExcutableCauseCargoExist
        {
            get => _IsChargeTaskNotExcutableCauseCargoExist;
            set
            {
                if (_IsChargeTaskNotExcutableCauseCargoExist != value)
                {
                    if (value)
                    {

                    }
                    else
                    {

                    }
                    _IsChargeTaskNotExcutableCauseCargoExist = value;
                }
            }
        }
        public AGV_ORDERABLE_STATUS OrderExecuteState
        {
            get => previous_OrderExecuteState;
            set
            {
                if (previous_OrderExecuteState != value)
                {
                    previous_OrderExecuteState = value;
                    LOG.INFO($"{agv.Name} Order Execute State Changed to {value}(System Run Mode={SystemModes.RunMode}", color: ConsoleColor.Green);

                    //if (value == AGV_ORDERABLE_STATUS.NO_ORDER && SystemModes.RunMode == RUN_MODE.RUN)
                    //{
                    //    if (agv.currentMapPoint.IsCharge)
                    //        return;
                    //    Task.Factory.StartNew(async () =>
                    //    {
                    //        //Delay一下再確認可不可充電
                    //        await Task.Delay(TimeSpan.FromSeconds(AGVSConfigulator.SysConfigs.AutoModeConfigs.AGVIdleTimeUplimitToExecuteChargeTask));

                    //        if (SystemModes.RunMode != RUN_MODE.RUN)
                    //            return;

                    //        if (agv.states.Cargo_Status == 1 || agv.states.CSTID.Any(id => id != ""))
                    //        {
                    //            var _charge_forbid_alarm = await AlarmManagerCenter.AddAlarmAsync(ALARMS.Cannot_Auto_Parking_When_AGV_Has_Cargo, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING, agv.Name, agv.currentMapPoint.Graph.Display);
                    //            WaitingToChargable(_charge_forbid_alarm);
                    //            return;
                    //        }
                    //        if (agv.main_state == clsEnums.MAIN_STATUS.IDLE)
                    //        {
                    //            CreateChargeTask();
                    //            await Task.Delay(200);
                    //        }
                    //    }, TaskCreationOptions.LongRunning);
                    //}
                }
            }
        }

        /// <summary>
        /// 自動派AGV去充電
        /// 條件 : 運轉模式
        /// </summary>
        protected virtual async void CheckAutoCharge()
        {
            Task<clsAlarmDto> _charge_forbid_alarm = null;
            while (true)
            {
                await Task.Delay(10);

                if (SystemModes.RunMode == RUN_MODE.MAINTAIN)
                    continue;

                if (_IsChargeStatesChecking)
                    continue;

                if (previous_OrderExecuteState != AGV_ORDERABLE_STATUS.NO_ORDER)
                    continue;

                if (agv.IsSolvingTrafficInterLock)
                    continue;

                if (agv.main_state == clsEnums.MAIN_STATUS.Charging)
                    continue;

                await Task.Delay(TimeSpan.FromSeconds(AGVSConfigulator.SysConfigs.AutoModeConfigs.AGVIdleTimeUplimitToExecuteChargeTask));
                if (agv.IsAGVCargoStatusCanNotGoToCharge() && !agv.currentMapPoint.IsCharge)
                {
                    if (_charge_forbid_alarm != null)
                        AlarmManagerCenter.RemoveAlarm(_charge_forbid_alarm.Result);
                    _charge_forbid_alarm = AlarmManagerCenter.AddAlarmAsync(ALARMS.Cannot_Auto_Parking_When_AGV_Has_Cargo, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING, agv.Name, agv.currentMapPoint.Graph.Display);
                    _charge_forbid_alarm.Wait();
                    continue;
                }

                bool any_charge_task_run_or_ready_to_run = taskList.Any(task => task.Action == ACTION_TYPE.Charge && (task.State == TASK_RUN_STATUS.WAIT || task.State == TASK_RUN_STATUS.NAVIGATING));
                if (any_charge_task_run_or_ready_to_run)
                    continue;

                if (agv.IsAGVIdlingAtChargeStationButBatteryLevelLow() || agv.IsAGVIdlingAtNormalPoint())
                {
                    if (_charge_forbid_alarm != null)
                        AlarmManagerCenter.RemoveAlarm(_charge_forbid_alarm.Result);
                    _charge_forbid_alarm = null;
                    CreateChargeTask();
                }
            }
        }
        // 原本自動充電相關function 如之後沒問題可刪
        //private void TryToCharge()
        //{
        //    if (agv.currentMapPoint.IsCharge)
        //        return;

        //    Task.Run(async () =>
        //    {
        //        //Delay一下再確認可不可充電
        //        await Task.Delay(TimeSpan.FromSeconds(AGVSConfigulator.SysConfigs.AutoModeConfigs.AGVIdleTimeUplimitToExecuteChargeTask));

        //        if (SystemModes.RunMode != RUN_MODE.RUN)
        //            return;

        //        if (agv.states.Cargo_Status == 1 || agv.states.CSTID.Any(id => id != ""))
        //        {
        //            var _charge_forbid_alarm = await AlarmManagerCenter.AddAlarmAsync(ALARMS.Cannot_Auto_Parking_When_AGV_Has_Cargo, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING, agv.Name, agv.currentMapPoint.Graph.Display);
        //            WaitingToChargable(_charge_forbid_alarm);
        //            return;
        //        }
        //        if (agv.main_state == clsEnums.MAIN_STATUS.IDLE)
        //        {
        //            CreateChargeTask();
        //            await Task.Delay(200);
        //        }
        //    });
        //    Task.Factory.StartNew(() => { }, TaskCreationOptions.LongRunning);
        //}

        //private async void WaitingToChargable(clsAlarmDto _charge_forbid_alarm)
        //{
        //    while (OrderExecuteState == AGV_ORDERABLE_STATUS.NO_ORDER && SystemModes.RunMode == RUN_MODE.RUN)
        //    {
        //        await Task.Delay(1000);
        //        if (agv.states.Cargo_Status == 1 || agv.states.CSTID.Any(id => id != ""))
        //            continue;

        //        CreateChargeTask();
        //        AlarmManagerCenter.RemoveAlarm(_charge_forbid_alarm);
        //        break;
        //    }
        //    LOG.INFO($"Wait AGV ({agv.Name}) Cargo Status is Chargable Process end.");
        //}

        private SemaphoreSlim _syncTaskQueueFronDBSemaphoreSlim = new SemaphoreSlim(1, 1);
        public async void AsyncTaskQueueFromDatabase()
        {
            await _syncTaskQueueFronDBSemaphoreSlim.WaitAsync();
            try
            {
                var taskIDs = taskList.Select(tk => tk.TaskName);
                if (!taskIDs.Any())
                    return;
                using (AGVSDatabase db = new AGVSDatabase())
                {
                    var taskInDB = db.tables.Tasks.Where(t => taskIDs.Contains(t.TaskName)).AsNoTracking();

                    if (!taskInDB.Any())
                        return;

                    var stateNoEqualTasks = taskList.Where(tk => tk.State != taskInDB.First(tk => tk.TaskName == tk.TaskName).State);
                    if (stateNoEqualTasks.Any())
                    {
                        var navagatings = stateNoEqualTasks.Where(tk => tk.State == TASK_RUN_STATUS.CANCEL || tk.State == TASK_RUN_STATUS.NAVIGATING);
                        if (navagatings.Any())
                        {
                            var indexs = navagatings.Select(task => taskList.FindIndex(t => t.TaskName == task.TaskName)).ToArray();
                            foreach (var idx in indexs)
                            {
                                LOG.TRACE($"{agv.Name}:{taskList[idx].TaskName} Remove from task queue");
                                taskList.RemoveAt(idx);
                            }
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                LOG.ERROR(ex.Message + ex.StackTrace);
            }
            finally
            {
                _syncTaskQueueFronDBSemaphoreSlim.Release();
            }

        }
        public async void TryAppendTasksToQueue(List<clsTaskDto> tasksCollection)
        {

            if (tasksCollection.Any(tk => tk.State == TASK_RUN_STATUS.CANCEL))
            {

            }
            try
            {
                var notInQuqueOrders = tasksCollection.FindAll(task => !taskList.Any(tk => tk.TaskName == task.TaskName))
                                                        .SkipWhile(tk => tk.DesignatedAGVName != agv.Name);

                if (notInQuqueOrders.Any())
                {
                    taskList.AddRange(notInQuqueOrders);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
            }
        }

        public virtual List<clsTaskDto> taskList { get; } = new List<clsTaskDto>();
        public MapPoint[] CurrentTrajectory
        {
            get
            {
                if (TaskStatusTracker.SubTaskTracking == null)
                    return new MapPoint[0];
                return TaskStatusTracker.SubTaskTracking.EntirePathPlan.ToArray();
            }
        }


        public List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

        protected TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();

        public Dictionary<int, List<MapPoint>> Dict_PathNearPoint { get; set; } = new Dictionary<int, List<MapPoint>>();

        public clsAGVTaskDisaptchModule()
        {
        }
        public clsAGVTaskDisaptchModule(IAGV agv)
        {
            this.agv = agv;

            if (agv.model == clsEnums.AGV_TYPE.INSPECTION_AGV)
                TaskStatusTracker = new clsAGVTaskTrakInspectionAGV();
            else
            {
                TaskStatusTracker = new clsAGVTaskTrack(this);
                //TaskStatusTracker.AgvSimulation = new clsAGVSimulation(this);
            }

            TaskStatusTracker.AGV = agv;
        }


        public async Task Run()
        {
            await TaskAssignWorker();
        }
        private AGV_ORDERABLE_STATUS GetAGVReceiveOrderStatus()
        {
            try
            {
                if (agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                    return AGV_ORDERABLE_STATUS.AGV_STATUS_ERROR;

                if (agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                    return AGV_ORDERABLE_STATUS.AGV_OFFLINE;

                if (SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
                {
                    if (this.TaskStatusTracker.WaitingForResume && this.TaskStatusTracker.transferProcess != VehicleMovementStage.Completed && this.TaskStatusTracker.transferProcess != VehicleMovementStage.Not_Start_Yet)
                        return AGV_ORDERABLE_STATUS.EXECUTING_RESUME;
                }
                if (!taskList.Any(tk => tk.DesignatedAGVName == agv.Name && tk.State == TASK_RUN_STATUS.WAIT || tk.State == TASK_RUN_STATUS.NAVIGATING))
                    return AGV_ORDERABLE_STATUS.NO_ORDER;
                if (taskList.Any(tk => tk.State == TASK_RUN_STATUS.NAVIGATING && tk.DesignatedAGVName == agv.Name) || agv.main_state == clsEnums.MAIN_STATUS.RUN)
                    return AGV_ORDERABLE_STATUS.EXECUTING;
                return AGV_ORDERABLE_STATUS.EXECUTABLE;
            }
            catch (Exception ex)
            {
                LOG.Critical(ex.Message + ex.StackTrace);
                return AGV_ORDERABLE_STATUS.SystemError;
            }
        }
        public OrderHandlerBase OrderHandler { get; set; } = new MoveToOrderHandler();
        protected virtual async Task TaskAssignWorker()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        OrderExecuteState = GetAGVReceiveOrderStatus();
                        switch (OrderExecuteState)
                        {
                            case AGV_ORDERABLE_STATUS.EXECUTABLE:
                                var taskOrderedByPriority = taskList.Where(tk => tk.State == TASK_RUN_STATUS.WAIT).OrderByDescending(task => task.Priority).OrderBy(task => task.RecieveTime).ToList();
                                taskOrderedByPriority = taskOrderedByPriority.Where(tk => tk.DesignatedAGVName == agv.Name).ToList();
                                if (!taskOrderedByPriority.Any())
                                {
                                    taskList.Clear();
                                    continue;
                                }
                                var _ExecutingTask = taskOrderedByPriority.First();
                                ALARMS alarm_code = ALARMS.NONE;

                                if (!CheckTaskOrderContentAndTryFindBestWorkStation(_ExecutingTask, out alarm_code))
                                {
                                    _ExecutingTask.State = TASK_RUN_STATUS.FAILURE;
                                    _ExecutingTask.FinishTime = DateTime.Now;
                                    TaskStatusTracker.RaiseTaskDtoChange(this, _ExecutingTask);
                                    await AlarmManagerCenter.AddAlarmAsync(alarm_code, ALARM_SOURCE.AGVS);
                                    continue;
                                }

                                agv.IsTrafficTaskExecuting = _ExecutingTask.DispatcherName.ToUpper() == "TRAFFIC";
                                _ExecutingTask.State = TASK_RUN_STATUS.NAVIGATING;
                                TaskStatusTracker.RaiseTaskDtoChange(this, _ExecutingTask);
                                //await ExecuteTaskAsync(_ExecutingTask);
                                OrderHandlerFactory factory = new OrderHandlerFactory();
                                OrderHandler = factory.CreateHandler(_ExecutingTask);
                                OrderHandler.StartOrder(agv);
                                OrderHandler.OnTaskCanceled += OrderHandler_OnTaskCanceled;
                                if (_ExecutingTask.Action == ACTION_TYPE.Charge)
                                    (OrderHandler as ChargeOrderHandler).onAGVChargeOrderDone += HandleAGVChargeTaskRedoRequest;

                                OrderExecuteState = AGV_ORDERABLE_STATUS.EXECUTING;
                                // _taskListFromAGVS.RemoveAt(_taskListFromAGVS.FindIndex(tk => tk.TaskName == _ExecutingTask.TaskName));
                                await Task.Delay(1000);
                                break;
                            case AGV_ORDERABLE_STATUS.EXECUTING:
                                break;
                            case AGV_ORDERABLE_STATUS.AGV_STATUS_ERROR:
                                OrderHandler.RunningTask.TrafficWaitingState.SetDisplayMessage("STATUS_ERROR");
                                break;
                            case AGV_ORDERABLE_STATUS.NO_ORDER:
                                bool isCharging = agv.main_state == clsEnums.MAIN_STATUS.Charging;
                                if (!_IsChargeStatesChecking)
                                    OrderHandler.RunningTask.TrafficWaitingState.SetDisplayMessage(isCharging ? "充電中.." : "IDLE");
                                break;
                            case AGV_ORDERABLE_STATUS.AGV_OFFLINE:
                                OrderHandler.RunningTask.TrafficWaitingState.SetDisplayMessage("OFFLINE");
                                break;
                            case AGV_ORDERABLE_STATUS.EXECUTING_RESUME:
                                using (var database = new AGVSDatabase())
                                {
                                    var _lastPauseTask = database.tables.Tasks.Where(f => f.DesignatedAGVName == agv.Name && f.TaskName == TaskStatusTracker.OrderTaskName).FirstOrDefault();
                                    if (_lastPauseTask != null)
                                    {
                                        await ExecuteTaskAsync(_lastPauseTask);

                                    }
                                }
                                break;
                            default:
                                break;
                        }
                        //int removeNum = _taskListFromAGVS.RemoveAll(task => task.State == TASK_RUN_STATUS.CANCEL || task.State == TASK_RUN_STATUS.FAILURE || task.State == TASK_RUN_STATUS.ACTION_FINISH);
                    }
                    catch (NoPathForNavigatorException ex)
                    {
                        ExecutingTaskName = "";
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.NO_PATH_FOR_NAVIGATION);
                        AlarmManagerCenter.AddAlarmAsync(ALARMS.TRAFFIC_ABORT);
                    }
                    catch (IlleagalTaskDispatchException ex)
                    {
                        LOG.Critical(ex);
                        ExecutingTaskName = "";
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL);
                    }
                    catch (Exception ex)
                    {
                        LOG.Critical(ex);
                        ExecutingTaskName = "";
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION, ALARMS.SYSTEM_ERROR, ex.Message);
                        AlarmManagerCenter.AddAlarmAsync(ALARMS.TRAFFIC_ABORT);
                    }
                    finally
                    {
                        await Task.Delay(1000);
                    }
                }
            });

            Thread AutoChargeThread = new Thread(() =>
            {
                CheckAutoCharge();
            });
            AutoChargeThread.Start();
        }

        private void OrderHandler_OnTaskCanceled(object? sender, OrderHandlerBase e)
        {
            OrderHandler.OnTaskCanceled -= OrderHandler_OnTaskCanceled;
            taskList.RemoveAll(task => task.TaskName == e.OrderData.TaskName);
        }

        private void HandleAGVChargeTaskRedoRequest(object? sender, ChargeOrderHandler orderHandler)
        {

            orderHandler.onAGVChargeOrderDone -= HandleAGVChargeTaskRedoRequest;
            if (agv.main_state != clsEnums.MAIN_STATUS.IDLE)
                return;
            ConfirmAGVChargeState(orderHandler);


        }
        private async Task ConfirmAGVChargeState(ChargeOrderHandler orderHandler)
        {
            try
            {
                _IsChargeStatesChecking = true;
                if (agv.batteryStatus >= IAGV.BATTERY_STATUS.MIDDLE_HIGH)
                    return;

                CancellationTokenSource stateIncorrectConfrimCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (agv.main_state == MAIN_STATUS.IDLE)
                {
                    orderHandler.RunningTask.TrafficWaitingState.SetDisplayMessage($"確認充電狀態中-{stopwatch.Elapsed.ToString("mm\\:ss")}");
                    await Task.Delay(1);
                    if (stateIncorrectConfrimCts.IsCancellationRequested)
                    {
                        LOG.WARN($"{agv.Name} 完成充電任務已經過20秒仍未充電 且電量低於強制充電設定，重新發起充電任務");
                        OrderHandler.OrderData.State = TASK_RUN_STATUS.WAIT;
                        VMSManager.HandleTaskDBChangeRequestRaising(this, orderHandler.OrderData);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                orderHandler.RunningTask.TrafficWaitingState.SetStatusNoWaiting();
                _IsChargeStatesChecking = false;
            }

        }
        private async void CreateChargeTask(int chargeStationTag = -1)
        {
            _ = await TaskDBHelper.Add(new clsTaskDto
            {
                Action = ACTION_TYPE.Charge,
                TaskName = $"Charge-{agv.Name}_{DateTime.Now.ToString("yyMMdd_HHmmssff")}",
                DispatcherName = "VMS_Idle",
                DesignatedAGVName = agv.Name,
                RecieveTime = DateTime.Now,
                To_Station = chargeStationTag + ""
            });
        }

        public clsAGVTaskTrack TaskStatusTracker { get; set; } = new clsAGVTaskTrack();
        public string ExecutingTaskName { get; set; } = "";

        public clsAGVTaskTrack LastNormalTaskPauseByAvoid { get; set; } = new clsAGVTaskTrack();

        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {

            bool IsResumeTransferTask = false;
            VehicleMovementStage lastTransferProcess = default;
            if (SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
            {
                IsResumeTransferTask = (executingTask.TaskName == TaskStatusTracker.OrderTaskName) && (this.TaskStatusTracker.transferProcess == VehicleMovementStage.Traveling_To_Destine || this.TaskStatusTracker.transferProcess == VehicleMovementStage.Traveling_To_Source);
                lastTransferProcess = LastNormalTaskPauseByAvoid.transferProcess;
            }
            if (LastNormalTaskPauseByAvoid != null && LastNormalTaskPauseByAvoid.OrderTaskName == executingTask.TaskName)
            {
                IsResumeTransferTask = (executingTask.TaskName == LastNormalTaskPauseByAvoid.OrderTaskName) && (this.LastNormalTaskPauseByAvoid.transferProcess == VehicleMovementStage.Traveling_To_Destine || this.LastNormalTaskPauseByAvoid.transferProcess == VehicleMovementStage.Traveling_To_Source);
                lastTransferProcess = LastNormalTaskPauseByAvoid.transferProcess;
            }

            try
            {
                TaskStatusTracker.OnTaskOrderStatusQuery -= TaskTrackerQueryOrderStatusCallback;
            }
            catch (Exception)
            {
            }
            TaskStatusTracker?.Dispose();
            if (agv.model != clsEnums.AGV_TYPE.INSPECTION_AGV && executingTask.Action == ACTION_TYPE.Measure)
                TaskStatusTracker = new clsAGVTaskTrakInspectionAGV() { AGV = agv };
            else
            {
                if (agv.model == clsEnums.AGV_TYPE.INSPECTION_AGV)
                    TaskStatusTracker = new clsAGVTaskTrakInspectionAGV() { AGV = agv };
                else
                    TaskStatusTracker = new clsAGVTaskTrack(this) { AGV = agv };
            }

            ExecutingTaskName = executingTask.TaskName;
            TaskStatusTracker.OnTaskOrderStatusQuery += TaskTrackerQueryOrderStatusCallback;
            await TaskStatusTracker.Start(agv, executingTask, IsResumeTransferTask, lastTransferProcess);
        }

        private TASK_RUN_STATUS TaskTrackerQueryOrderStatusCallback(string taskName)
        {
            var _taskEntity = taskList.FirstOrDefault(task => task.TaskName == taskName);
            if (_taskEntity != null)
                return _taskEntity.State;
            else
                return TASK_RUN_STATUS.FAILURE;
        }

        public async Task<int> TaskFeedback(FeedbackData feedbackData)
        {
            var task_tracking = taskList.Where(task => task.TaskName == feedbackData.TaskName).FirstOrDefault();
            if (task_tracking == null)
            {
                //AsyncTaskQueueFromDatabase();
                LOG.WARN($"{agv.Name} task feedback, but order already not tracking");
                return 0;
            }

            var task_status = feedbackData.TaskStatus;
            if (task_status == TASK_RUN_STATUS.ACTION_FINISH || task_status == TASK_RUN_STATUS.FAILURE || task_status == TASK_RUN_STATUS.CANCEL || task_status == TASK_RUN_STATUS.NO_MISSION)
            {
                ExecutingTaskName = "";
                agv.IsTrafficTaskExecuting = false;
            }
            //_ = Task.Run(() => TaskStatusTracker.HandleAGVFeedback(feedbackData));
            OrderHandler.HandleAGVFeedbackAsync(feedbackData);
            return 0;
        }

        public async Task<SimpleRequestResponse> PostTaskRequestToAGVAsync(clsTaskDownloadData data)
        {
            try
            {
                if (agv.options.Simulation)
                    return new SimpleRequestResponse();
                //return await AgvSimulation.ActionRequestHandler(data);

                SimpleRequestResponse taskStateResponse = await agv.AGVHttp.PostAsync<SimpleRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", data);
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



        public void DispatchTrafficTask(clsTaskDownloadData task_download_data)
        {
            var _ExecutingTask = new clsTaskDto()
            {
                DesignatedAGVName = agv.Name,
                DispatcherName = "Traffic",
                Carrier_ID = task_download_data.CST.FirstOrDefault().CST_ID,
                TaskName = task_download_data.Task_Name,
                Priority = 10,
                Action = task_download_data.Action_Type,
                To_Station = task_download_data.Destination.ToString(),
                RecieveTime = DateTime.Now,
                State = TASK_RUN_STATUS.WAIT,
                IsTrafficControlTask = true,
            };
            TaskDBHelper.Add(_ExecutingTask);
        }

        public async Task<string> CancelTask(bool unRegistPoints = true)
        {
            this.OrderHandler.CancelOrder();
            return "";
            //return await TaskStatusTracker.CancelOrder(unRegistPoints);
        }
        private List<IAGV> WaitingForYieldedAGVList = new List<IAGV>();
        public WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY AGVWaitingYouNotify(IAGV waiting_for_move_agv)
        {
            if (OrderExecuteState == AGV_ORDERABLE_STATUS.EXECUTING)
            {

                if (WaitingForYieldedAGVList.Contains(waiting_for_move_agv))
                    return WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_WAIT;

                bool isMoving = this.OrderHandler.RunningTask.ActionType == ACTION_TYPE.None;
                if (!isMoving)
                    return WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_WAIT;


                //若這部AGV目前的軌跡終點將不會阻擋
                var _waiting_agv_order_handler = waiting_for_move_agv.taskDispatchModule.OrderHandler;
                ACTION_TYPE order_action_of_waiting_agv = _waiting_agv_order_handler.OrderAction;
                ACTION_TYPE order_action_of_be_request_agv = this.OrderHandler.OrderAction;
                var remainTags_of_yield_AGV = this.OrderHandler.RunningTask.MoveTaskEvent.AGVRequestState.RemainTagList;
                remainTags_of_yield_AGV = remainTags_of_yield_AGV.Skip(remainTags_of_yield_AGV.IndexOf(this.agv.states.Last_Visited_Node)).ToList();
                int destineOfCurrentpath = remainTags_of_yield_AGV.Count == 0 ? this.agv.states.Last_Visited_Node : remainTags_of_yield_AGV.Last();

                if (order_action_of_be_request_agv == ACTION_TYPE.Charge)
                {
                    int chargeStationTag = this.OrderHandler.OrderData.To_Station_Tag;
                    int secondaryTagOfChargeStation = StaMap.GetPointByIndex(StaMap.GetPointByTagNumber(chargeStationTag).Target.Keys.First()).TagNumber;
                    if (secondaryTagOfChargeStation == this.agv.states.Last_Visited_Node)
                        return WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_WAIT;
                }

                bool notBlockedlater = !_waiting_agv_order_handler.RunningTask.MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList.Contains(destineOfCurrentpath);
                bool waitingAGVBlockedYieldAGV = remainTags_of_yield_AGV.Contains(waiting_for_move_agv.states.Last_Visited_Node);

                if (notBlockedlater && !waitingAGVBlockedYieldAGV)
                    return WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_WAIT;


                if (order_action_of_waiting_agv == ACTION_TYPE.Charge)
                    return WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_YIELD_ME;

                OrderHandler.StartTrafficControl();
                //先實作退讓
                WaitingForYieldedAGVList.Add(waiting_for_move_agv);
                this.OrderHandler.RunningTask.WaitingForAGV = WaitingForYieldedAGVList;

                StartYieldAGVAction(waiting_for_move_agv);

            }
            return WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_WAIT;
        }

        /// <summary>
        /// 開始進行讓路
        /// </summary>
        /// <param name="waiting_for_move_agv"></param>
        private void StartYieldAGVAction(IAGV waiting_for_move_agv)
        {
            Task.Run(async () =>
            {
                TrafficEventCommanderFactory factory = new TrafficEventCommanderFactory();
                var commander = factory.CreateYieldWayEventWhenAGVMovingCommander(waiting_for_move_agv, this.agv);
                var _solveReuslt = await commander.StartSolve();
                OrderHandler.FinishTrafficControl();
                if (_solveReuslt.Status != clsSolverResult.SOLVER_RESULT.SUCCESS)
                {
                    return;
                }
                WaitAGVPass(waiting_for_move_agv);
                LOG.TRACE($"Continue Task-{this.OrderHandler.OrderData.TaskName} {this.agv.main_state}");
                this.OrderHandler.RunningTask.DistpatchToAGV();
                void WaitAGVPass(IAGV waiting_for_move_agv)
                {
                    this.OrderHandler.RunningTask.CreateTaskToAGV();
                    var nextTrajTags = (this.OrderHandler.RunningTask as MoveTask).TaskSequenceList.Last().GetTagCollection();
                    var remainTags = nextTrajTags.Skip(nextTrajTags.ToList().IndexOf(this.agv.states.Last_Visited_Node) + 1).ToList();
                    var _waiting_tags = remainTags.ToList();
                    while (remainTags.Contains(waiting_for_move_agv.states.Last_Visited_Node))
                    {
                        _waiting_tags.Remove(waiting_for_move_agv.states.Last_Visited_Node);
                        this.OrderHandler.RunningTask.TrafficWaitingState.SetStatusWaitingConflictPointRelease(_waiting_tags);
                        LOG.TRACE($"Finish Traffic Control..waiting {string.Join(",", _waiting_tags)} release  >>> {waiting_for_move_agv.Name}(Current Tag={waiting_for_move_agv.states.Last_Visited_Node}) and order will continue...{this.agv.main_state}");
                        Thread.Sleep(1000);
                    }
                    this.OrderHandler.RunningTask.TrafficWaitingState.SetStatusNoWaiting();
                }
            });
        }

        public void AGVNotWaitingYouNotify(IAGV agv)
        {
            WaitingForYieldedAGVList.Remove(agv);
            this.OrderHandler.RunningTask.WaitingForAGV = WaitingForYieldedAGVList;
        }

    }
}

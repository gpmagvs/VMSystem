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
        }

        public IAGV agv;
        private PathFinder pathFinder = new PathFinder();
        private DateTime LastNonNoOrderTime;
        private bool _IsChargeTaskCreating;

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
                    LOG.Critical($"{agv.Name} Order Execute State Changed to {value}(System Run Mode={SystemModes.RunMode})");

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
        private async void CheckAutoCharge()
        {
            Task<clsAlarmDto> _charge_forbid_alarm = null;
            while (true)
            {
                Thread.Sleep(10);

                if (SystemModes.RunMode == RUN_MODE.MAINTAIN)
                    continue;

                if (previous_OrderExecuteState != AGV_ORDERABLE_STATUS.NO_ORDER)
                    continue;

                if (agv.IsSolvingTrafficInterLock)
                    continue;

                if (agv.main_state == clsEnums.MAIN_STATUS.Charging)
                    continue;

                Thread.Sleep(TimeSpan.FromSeconds(AGVSConfigulator.SysConfigs.AutoModeConfigs.AGVIdleTimeUplimitToExecuteChargeTask));
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

        public List<clsTaskDto> _taskList = new List<clsTaskDto>();
        public virtual List<clsTaskDto> taskList
        {
            get => _taskList;
            set
            {
                _taskList = value;
                if (value.Count != 0)
                {
                    try
                    {
                        var chargeTaskNow = value.FirstOrDefault(tk => tk.TaskName == ExecutingTaskName && tk.Action == ACTION_TYPE.Charge);
                        if (chargeTaskNow != null && SystemModes.RunMode == RUN_MODE.RUN && chargeTaskNow.State != TASK_RUN_STATUS.CANCEL)
                        {
                            if (value.FindAll(tk => tk.Action != ACTION_TYPE.Charge && tk.TaskName != ExecutingTaskName).Count > 0) //有其他非充電任務產生
                            {
                                CancelTask(false);

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                    }
                }
                if (ExecutingTaskName != null)
                {

                }
            }
        }

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

            if (agv.model == clsEnums.AGV_MODEL.INSPECTION_AGV)
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
            TaskAssignWorker();
        }
        private AGV_ORDERABLE_STATUS GetAGVReceiveOrderStatus()
        {
            if (agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                return AGV_ORDERABLE_STATUS.AGV_OFFLINE;
            if (agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                return AGV_ORDERABLE_STATUS.AGV_STATUS_ERROR;
            if (agv.main_state == clsEnums.MAIN_STATUS.RUN)
                return AGV_ORDERABLE_STATUS.EXECUTING;
            if (SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
            {
                if (this.TaskStatusTracker.WaitingForResume && this.TaskStatusTracker.transferProcess != TRANSFER_PROCESS.FINISH && this.TaskStatusTracker.transferProcess != TRANSFER_PROCESS.NOT_START_YET)
                    return AGV_ORDERABLE_STATUS.EXECUTING_RESUME;
            }
            if (taskList.Where(tk => tk.State == TASK_RUN_STATUS.WAIT || tk.State == TASK_RUN_STATUS.NAVIGATING).Count() == 0)
                return AGV_ORDERABLE_STATUS.NO_ORDER;
            if (taskList.Any(tk => tk.State == TASK_RUN_STATUS.NAVIGATING && tk.DesignatedAGVName == agv.Name))
                return AGV_ORDERABLE_STATUS.EXECUTING;
            return AGV_ORDERABLE_STATUS.EXECUTABLE;
        }

        protected virtual async Task TaskAssignWorker()
        {
            Thread OrderMonitorThread = new Thread(async () =>
            {
                while (true)
                {
                    Thread.Sleep(100);
                    try
                    {
                        OrderExecuteState = GetAGVReceiveOrderStatus();
                        if (OrderExecuteState == AGV_ORDERABLE_STATUS.EXECUTABLE)
                        {
                            var taskOrderedByPriority = taskList.Where(tk => tk.State == TASK_RUN_STATUS.WAIT).OrderByDescending(task => task.Priority).OrderBy(task => task.RecieveTime);
                            var _ExecutingTask = taskOrderedByPriority.First();

                            if (!CheckTaskOrderContentAndTryFindBestWorkStation(_ExecutingTask, out ALARMS alarm_code))
                            {
                                await AlarmManagerCenter.AddAlarmAsync(alarm_code, ALARM_SOURCE.AGVS);
                                continue;
                            }
                            agv.IsTrafficTaskExecuting = _ExecutingTask.DispatcherName.ToUpper() == "TRAFFIC";
                            _ExecutingTask.State = TASK_RUN_STATUS.NAVIGATING;
                            TaskStatusTracker.RaiseTaskDtoChange(this, _ExecutingTask);
                            await ExecuteTaskAsync(_ExecutingTask);
                        }

                        else if (OrderExecuteState == AGV_ORDERABLE_STATUS.EXECUTING_RESUME)
                        {
                            using (var database = new AGVSDatabase())
                            {
                                var _lastPauseTask = database.tables.Tasks.Where(f => f.DesignatedAGVName == agv.Name && f.TaskName == TaskStatusTracker.OrderTaskName).FirstOrDefault();
                                if (_lastPauseTask != null)
                                {
                                    await ExecuteTaskAsync(_lastPauseTask);

                                }
                            }
                        }
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
                }
            });
            OrderMonitorThread.Start();

            Thread AutoChargeThread = new Thread(() =>
            {
                CheckAutoCharge();
            });
            AutoChargeThread.Start();
        }

        private async void CreateChargeTask()
        {
            _ = await TaskDBHelper.Add(new clsTaskDto
            {
                Action = ACTION_TYPE.Charge,
                TaskName = $"Charge_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                DispatcherName = "VMS_Idle",
                DesignatedAGVName = agv.Name,
                RecieveTime = DateTime.Now,
            });
        }

        public clsAGVTaskTrack TaskStatusTracker { get; set; } = new clsAGVTaskTrack();
        public string ExecutingTaskName { get; set; } = "";

        public clsAGVTaskTrack LastNormalTaskPauseByAvoid { get; set; } = new clsAGVTaskTrack();

        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {

            bool IsResumeTransferTask = false;
            TRANSFER_PROCESS lastTransferProcess = default;
            if (SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
            {
                IsResumeTransferTask = (executingTask.TaskName == TaskStatusTracker.OrderTaskName) && (this.TaskStatusTracker.transferProcess == TRANSFER_PROCESS.GO_TO_DESTINE_EQ | this.TaskStatusTracker.transferProcess == TRANSFER_PROCESS.GO_TO_SOURCE_EQ);
                lastTransferProcess = LastNormalTaskPauseByAvoid.transferProcess;
            }
            if (LastNormalTaskPauseByAvoid != null && LastNormalTaskPauseByAvoid.OrderTaskName == executingTask.TaskName)
            {
                IsResumeTransferTask = (executingTask.TaskName == LastNormalTaskPauseByAvoid.OrderTaskName) && (this.LastNormalTaskPauseByAvoid.transferProcess == TRANSFER_PROCESS.GO_TO_DESTINE_EQ | this.LastNormalTaskPauseByAvoid.transferProcess == TRANSFER_PROCESS.GO_TO_SOURCE_EQ);
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
            if (agv.model != clsEnums.AGV_MODEL.INSPECTION_AGV && executingTask.Action == ACTION_TYPE.Measure)
                TaskStatusTracker = new clsAGVTaskTrakInspectionAGV() { AGV = agv };
            else
            {
                if (agv.model == clsEnums.AGV_MODEL.INSPECTION_AGV)
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
                LOG.WARN($"{agv.Name} task feedback, but order already not tracking");
                return 0;
            }

            var task_status = feedbackData.TaskStatus;
            if (task_status == TASK_RUN_STATUS.ACTION_FINISH || task_status == TASK_RUN_STATUS.FAILURE || task_status == TASK_RUN_STATUS.CANCEL || task_status == TASK_RUN_STATUS.NO_MISSION)
            {
                ExecutingTaskName = "";
                agv.IsTrafficTaskExecuting = false;
            }
            _ = Task.Run(() => TaskStatusTracker.HandleAGVFeedback(feedbackData));
            return 0;
        }

        /// <summary>
        /// 
        /// </summary>       
        /// <param name="EQTag">取放貨設備的Tag</param>
        /// <param name="LDULD">0:load , 1:unlod</param>
        private async Task LDULDFinishReport(int EQTag, int LDULD)
        {
            await agv.AGVHttp.PostAsync<object, object>($"/api/Task/LDULDFinishFeedback?agv_name={agv.Name}&EQTag={EQTag}&LDULD={LDULD}", null);
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
            return await TaskStatusTracker.CancelOrder(unRegistPoints);
        }

    }
}

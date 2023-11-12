using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpTools;
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
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using AGVSystemCommonNet6.AGVDispatch.RunMode;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ObjectiveC;

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

        private AGV_ORDERABLE_STATUS previous_OrderExecuteState = AGV_ORDERABLE_STATUS.NO_ORDER;
        public AGV_ORDERABLE_STATUS OrderExecuteState
        {
            get => previous_OrderExecuteState;
            set
            {
                if (previous_OrderExecuteState != value)
                {
                    previous_OrderExecuteState = value;
                    LOG.TRACE($"{agv.Name} Order Execute State Changed to {value}(System Run Mode={SystemModes.RunMode})");
                    if (value == AGV_ORDERABLE_STATUS.NO_ORDER)
                    {
                        if (SystemModes.RunMode == RUN_MODE.RUN && !agv.currentMapPoint.IsCharge)
                        {
                            if (agv.states.Cargo_Status != 0)
                            {
                                AlarmManagerCenter.AddAlarmAsync(ALARMS.Cannot_Auto_Parking_When_AGV_Has_Cargo, level: ALARM_LEVEL.WARNING, Equipment_Name: agv.Name, location: agv.currentMapPoint.Name);
                                return;
                            }
                            LOG.TRACE($"{agv.Name} Order Execute State is {value} and RUN Mode={SystemModes.RunMode},AGV Not act Charge Station, Raise Charge Task To AGV.");
                            TaskDBHelper.Add(new clsTaskDto
                            {
                                Action = ACTION_TYPE.Charge,
                                TaskName = $"Charge_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                                DispatcherName = "VMS",
                                DesignatedAGVName = agv.Name,
                                RecieveTime = DateTime.Now,
                            });
                        }
                    }
                }
            }
        }

        public virtual List<clsTaskDto> taskList { get; set; } = new List<clsTaskDto>();
        public static event EventHandler<clsTaskDto> OnTaskDBChangeRequestRaising;

        public MapPoint[] CurrentTrajectory
        {
            get
            {
                return TaskStatusTracker.SubTaskTracking.EntirePathPlan.ToArray();
            }
        }

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

            if (agv.model == clsEnums.AGV_MODEL.INSPECTION_AGV)
                TaskStatusTracker = new clsAGVTaskTrakInspectionAGV();
            else
            {
                TaskStatusTracker = new clsAGVTaskTrack(this);
                //TaskStatusTracker.AgvSimulation = new clsAGVSimulation(this);
            }

            TaskStatusTracker.AGV = agv;
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

            using (var database = new AGVSDatabase())
            {
                taskList = database.tables.Tasks.AsNoTracking().Where(f => (f.State == TASK_RUN_STATUS.WAIT | f.State == TASK_RUN_STATUS.NAVIGATING) && f.DesignatedAGVName == agv.Name).OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
            }
            if (taskList.Count == 0)
                return AGV_ORDERABLE_STATUS.NO_ORDER;
            if (taskList.Any(tk => tk.State == TASK_RUN_STATUS.NAVIGATING && tk.DesignatedAGVName == agv.Name))
                return AGV_ORDERABLE_STATUS.EXECUTING;
            return AGV_ORDERABLE_STATUS.EXECUTABLE;
        }
        protected virtual void TaskAssignWorker()
        {

            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    await Task.Delay(200);
                    try
                    {
                        OrderExecuteState = GetAGVReceiveOrderStatus();
                        if (OrderExecuteState == AGV_ORDERABLE_STATUS.EXECUTABLE)
                        {
                            var taskOrderedByPriority = taskList.OrderByDescending(task => task.Priority).OrderBy(task => task.RecieveTime);
                            var _ExecutingTask = taskOrderedByPriority.First();

                            if (_ExecutingTask.Action == ACTION_TYPE.Charge)
                            {
                                Thread.Sleep(200);
                                using (var database = new AGVSDatabase())
                                {
                                    taskList = database.tables.Tasks.Where(f => (f.State == TASK_RUN_STATUS.WAIT | f.State == TASK_RUN_STATUS.NAVIGATING) && f.DesignatedAGVName == agv.Name).OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
                                    if (taskList.Any(task => task.Action == ACTION_TYPE.Carry))
                                    {
                                        var chare_task = database.tables.Tasks.First(tk => tk.TaskName == _ExecutingTask.TaskName);
                                        chare_task.FailureReason = "Transfer Task Is Raised";
                                        chare_task.FinishTime = DateTime.Now;
                                        chare_task.State = TASK_RUN_STATUS.CANCEL;
                                        OnTaskDBChangeRequestRaising?.Invoke(this, chare_task);
                                        continue;
                                    }
                                }
                            }
                            if (!CheckTaskOrderContentAndTryFindBestWorkStation(_ExecutingTask, out ALARMS alarm_code))
                            {
                                await AlarmManagerCenter.AddAlarmAsync(alarm_code, ALARM_SOURCE.AGVS);
                                continue;
                            }
                            agv.IsTrafficTaskExecuting = _ExecutingTask.DispatcherName.ToUpper() == "TRAFFIC";
                            _ExecutingTask.State = TASK_RUN_STATUS.NAVIGATING;
                            OnTaskDBChangeRequestRaising?.Invoke(this, _ExecutingTask);
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
                        ExecutingTaskName = "";
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION, ALARMS.SYSTEM_ERROR, ex.Message);
                        AlarmManagerCenter.AddAlarmAsync(ALARMS.TRAFFIC_ABORT);
                    }

                }
            });
        }
        public clsAGVTaskTrack TaskStatusTracker { get; set; } = new clsAGVTaskTrack();
        public string ExecutingTaskName { get; set; } = "";

        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {
            TaskStatusTracker?.Dispose();

            bool IsResumeTransferTask = false;
            TRANSFER_PROCESS lastTransferProcess = default;
            if (SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
            {
                IsResumeTransferTask = (executingTask.TaskName == TaskStatusTracker.OrderTaskName) && (this.TaskStatusTracker.transferProcess == TRANSFER_PROCESS.GO_TO_DESTINE_EQ | this.TaskStatusTracker.transferProcess == TRANSFER_PROCESS.GO_TO_SOURCE_EQ);
                lastTransferProcess = TaskStatusTracker.transferProcess;
            }


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
            await TaskStatusTracker.Start(agv, executingTask, IsResumeTransferTask, lastTransferProcess);
        }



        public async Task<int> TaskFeedback(FeedbackData feedbackData)
        {
            var task_status = feedbackData.TaskStatus;
            if (task_status == TASK_RUN_STATUS.ACTION_FINISH | task_status == TASK_RUN_STATUS.FAILURE | task_status == TASK_RUN_STATUS.CANCEL | task_status == TASK_RUN_STATUS.NO_MISSION)
            {
                ExecutingTaskName = "";
                agv.IsTrafficTaskExecuting = false;
            }
            using (var db = new AGVSDatabase())
            {
                var task_tracking = db.tables.Tasks.Where(task => task.TaskName == feedbackData.TaskName).FirstOrDefault();
                if (task_tracking == null)
                {
                    LOG.WARN($"{agv.Name} task feedback, but order already not tracking");
                    return 0;
                }
                else
                {
                    if (task_tracking.State != TASK_RUN_STATUS.NAVIGATING)
                        return 0;
                }
                var response = await TaskStatusTracker.HandleAGVFeedback(feedbackData);

                return (int)response;
            }

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
            var _ExecutingTask = new AGVSystemCommonNet6.TASK.clsTaskDto()
            {
                DesignatedAGVName = agv.Name,
                DispatcherName = "Traffic",
                Carrier_ID = task_download_data.CST.FirstOrDefault().CST_ID,
                TaskName = task_download_data.Task_Name,
                Priority = 10,
                Action = task_download_data.Action_Type,
                To_Station = task_download_data.Destination.ToString(),
                RecieveTime = DateTime.Now,
                State = TASK_RUN_STATUS.WAIT
            };
            TaskDBHelper.Add(_ExecutingTask);
        }

        public void CancelTask()
        {
            TaskStatusTracker.CancelOrder();
        }


    }
}

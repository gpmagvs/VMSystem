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
                    LOG.TRACE($"{agv.Name} Order Execute State Changed to {value}");
                }
            }
        }

        public virtual List<clsTaskDto> taskList { get; set; } = new List<clsTaskDto>();

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

            if (agv.model == clsEnums.AGV_MODEL.INSPECTION_AGV)
                TaskStatusTracker = new clsAGVTaskTrakInspectionAGV();
            else
                TaskStatusTracker = new clsAGVTaskTrack();

            TaskStatusTracker.AGV = agv;
        }


        private AGV_ORDERABLE_STATUS GetAGVReceiveOrderStatus()
        {
            using (var database = new AGVSDatabase())
            {
                taskList = database.tables.Tasks.AsNoTracking().Where(f => (f.State == TASK_RUN_STATUS.WAIT | f.State == TASK_RUN_STATUS.NAVIGATING) && f.DesignatedAGVName == agv.Name).OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
            }
            if (agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                return AGV_ORDERABLE_STATUS.AGV_OFFLINE;
            if (agv.main_state != clsEnums.MAIN_STATUS.IDLE && agv.main_state != clsEnums.MAIN_STATUS.Charging)
                return AGV_ORDERABLE_STATUS.AGV_STATUS_ERROR;
            if (taskList.Count == 0)
                return AGV_ORDERABLE_STATUS.NO_ORDER;
            if (taskList.Any(task => task.State == TASK_RUN_STATUS.NAVIGATING))
                return AGV_ORDERABLE_STATUS.EXECUTING;
            return AGV_ORDERABLE_STATUS.EXECUTABLE;
        }
        protected virtual void TaskAssignWorker()
        {

            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(10);

                    try
                    {
                        OrderExecuteState = GetAGVReceiveOrderStatus();
                        if (OrderExecuteState == AGV_ORDERABLE_STATUS.EXECUTABLE)
                        {
                            var taskOrderedByPriority = taskList.OrderByDescending(task => task.Priority);
                            var _ExecutingTask = taskOrderedByPriority.First();

                            if (!CheckTaskOrderContentAndTryFindBestWorkStation(_ExecutingTask, out ALARMS alarm_code))
                            {
                                AlarmManagerCenter.AddAlarm(alarm_code, ALARM_SOURCE.AGVS);
                                continue;
                            }
                            await Task.Delay(1000);
                            await ExecuteTaskAsync(_ExecutingTask);
                        }
                    }
                    catch (NoPathForNavigatorException ex)
                    {
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.NO_PATH_FOR_NAVIGATION);
                        AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_ABORT);
                    }
                    catch (IlleagalTaskDispatchException ex)
                    {
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL);
                    }
                    catch (Exception ex)
                    {
                        TaskStatusTracker.AbortOrder(TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION, ex.Message);
                        AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_ABORT);
                    }

                }
            });
        }
        public clsAGVTaskTrack TaskStatusTracker { get; set; } = new clsAGVTaskTrack();
        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {
            await TaskStatusTracker.Start(agv, executingTask);
        }



        public async Task<int> TaskFeedback(FeedbackData feedbackData)
        {
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
                    return await AgvSimulation.ActionRequestHandler(data);

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

        public void CancelTask()
        {
            TaskStatusTracker.CancelOrder();
        }


    }
}

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
using static AGVSystemCommonNet6.MAP.PathFinder;
using VMSystem.AGV.TaskDispatch;

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
        public AGV_ORDERABLE_STATUS OrderExecuteState => GetAGVReceiveOrderStatus();
        private string HttpHost => $"http://{agv.options.HostIP}:{agv.options.HostPort}";
        public virtual List<clsTaskDto> taskList
        {
            get
            {
                return TaskDBHelper.GetALLInCompletedTask().FindAll(f => f.State != TASK_RUN_STATUS.FAILURE && f.DesignatedAGVName == agv.Name);
            }
        }
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
            TaskStatusTracker = new clsAGVTaskTrack()
            {
                AGV = agv
            };
        }

        public void AddTask(clsTaskDto taskDto)
        {

            taskList.Add(taskDto);
        }

        private AGV_ORDERABLE_STATUS GetAGVReceiveOrderStatus()
        {
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
                    await Task.Delay(1000);
                    try
                    {
                        if (OrderExecuteState != AGV_ORDERABLE_STATUS.EXECUTABLE)
                            continue;


                        var taskOrderedByPriority = taskList.OrderByDescending(task => task.Priority);
                        var _ExecutingTask = taskOrderedByPriority.First();

                        if (!BeforeDispatchTaskWorkCheck(_ExecutingTask, out ALARMS alarm_code))
                        {
                            AlarmManagerCenter.AddAlarm(alarm_code, ALARM_SOURCE.AGVS);
                            continue;
                        }
                        await ExecuteTaskAsync(_ExecutingTask);
                    }
                    catch (NoPathForNavigatorException ex)
                    {
                        TaskStatusTracker.AbortOrder();
                        AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_ABORT);
                    }
                    catch (IlleagalTaskDispatchException ex)
                    {
                        TaskStatusTracker.AbortOrder();
                    }
                    catch (Exception ex)
                    {
                        TaskStatusTracker.AbortOrder();
                        AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_ABORT);
                    }

                }
            });
        }
        public clsAGVTaskTrack TaskStatusTracker { get; set; } = new clsAGVTaskTrack();
        private async Task ExecuteTaskAsync(clsTaskDto executingTask)
        {
            TaskStatusTracker.Start(agv, executingTask);
        }



        public int TaskFeedback(FeedbackData feedbackData, out string message)
        {
            message = "";
            TaskStatusTracker.HandleAGVFeedback(feedbackData);
            return 0;
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

                SimpleRequestResponse taskStateResponse = await Http.PostAsync<SimpleRequestResponse,clsTaskDownloadData>($"{HttpHost}/api/TaskDispatch/Execute", data);
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

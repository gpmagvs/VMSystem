using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using NLog;
using NLog.Fluent;
using VMSystem.AGV.TaskDispatch.Tasks;
using static AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions;

namespace VMSystem.AGV
{
    /// <summary>
    /// 透過API對車輛進行任務發送、取消
    /// </summary>
    public class TaskExecuteHelper
    {
        public clsAGV Vehicle { get; }

        /// <summary>
        /// 最後一次下發的任務ID
        /// </summary>
        public string ExecutingTaskName { get; private set; } = "";

        /// <summary>
        /// 最後一次下發給車輛的任務模型
        /// </summary>
        public clsTaskDownloadData lastTaskDonwloadToAGV { get; private set; } = null;

        private HttpHelper VehicleHttp => Vehicle.AGVHttp;
        private AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL CommunicationProtocol => Vehicle.options.Protocol;

        private Logger logger;

        internal ManualResetEvent WaitACTIONFinishReportedMRE = new ManualResetEvent(false);

        private SemaphoreSlim TaskExecuteSemaphoreSlim = new SemaphoreSlim(1, 1);

        public TaskExecuteHelper(clsAGV vehicle)
        {
            Vehicle = vehicle;
            logger = NLog.LogManager.GetLogger($"TaskExecuteHelper/{vehicle.Name}");
            logger.Trace("TaskExecuterHelper instance created");
        }

        /// <summary>
        /// 任務下發給車輛
        /// </summary>
        /// <param name="task"></param>
        /// <param name="_TaskDonwloadToAGV"></param>
        /// <returns></returns>
        internal async Task<TaskDownloadRequestResponse> TaskDownload(TaskBase task, clsTaskDownloadData _TaskDonwloadToAGV)
        {
            try
            {
                await TaskExecuteSemaphoreSlim.WaitAsync();
                if (_TaskDonwloadToAGV.Action_Type == ACTION_TYPE.None && lastTaskDonwloadToAGV != null)
                {
                    //check 是否非完整任務會造成空車派車生成路徑錯誤
                    //前次下發非完整任務，若當前任務的起點與前次任務的終點不同，則需先Cycle Stop 且需要將任務軌跡重整

                    bool _IsTaskTrajectoryIncorrect()
                    {
                        int destineTagOfNewTask = _TaskDonwloadToAGV.Destination;
                        int destineTagOfLastTask = lastTaskDonwloadToAGV.Destination;

                        int tagOfFirstPointOfNewTask = _TaskDonwloadToAGV.ExecutingTrajecory.First().Point_ID;
                        int tagOfFirstPointOfLastTask = lastTaskDonwloadToAGV.ExecutingTrajecory.First().Point_ID;
                        return (tagOfFirstPointOfNewTask != tagOfFirstPointOfLastTask) || destineTagOfNewTask != destineTagOfLastTask;
                    }
                    bool isPathError = lastTaskDonwloadToAGV.IsSegmentTask && _IsTaskTrajectoryIncorrect();
                    bool isPreviousTaskNotComplete = !lastTaskDonwloadToAGV.IsSegmentTask && Vehicle.main_state == clsEnums.MAIN_STATUS.RUN;//前次為完整任務但還沒執行完
                    if (isPathError || isPreviousTaskNotComplete)//前次為非完整任務
                    {
                        string logMsg = isPathError ? $"派車生成路徑錯誤!下發Cycle Stop First" : "前次完整任務未完成!下發Cycle Stop First";
                        logger.Warn(logMsg);
                        NotifyServiceHelper.WARNING(logMsg);

                        await TaskCycleStop(lastTaskDonwloadToAGV.Task_Name);
                        //重新生成軌跡
                        int vehicleCurrentTag = Vehicle.states.Last_Visited_Node;
                        if (_TaskDonwloadToAGV.Action_Type == ACTION_TYPE.None)
                        {
                            int newTrajStartIndex = _TaskDonwloadToAGV.Trajectory.ToList().FindIndex(p => p.Point_ID == vehicleCurrentTag);
                            _TaskDonwloadToAGV.Trajectory = _TaskDonwloadToAGV.Trajectory.Skip(newTrajStartIndex).ToArray();
                        }
                    }
                }


                clsTaskDto order = task.OrderData;
                _TaskDonwloadToAGV.OrderInfo = new clsTaskDownloadData.clsOrderInfo
                {
                    ActionName = order.Action,
                    NextAction = task.NextAction,
                    SourceTag = order.Action == ACTION_TYPE.Carry ? order.From_Station_Tag : order.To_Station_Tag,
                    DestineTag = order.To_Station_Tag,
                    DestineName = StaMap.GetPointByTagNumber(order.To_Station_Tag).Graph.Display,
                    SourceName = order.Action == ACTION_TYPE.Carry ? StaMap.GetPointByTagNumber(order.From_Station_Tag).Graph.Display : "",
                    IsTransferTask = order.Action == ACTION_TYPE.Carry,
                    DestineSlot = int.Parse(order.To_Slot),
                    SourceSlot = int.Parse(order.From_Slot)
                };

                logger.Info($"Trajectory send to AGV = {string.Join("->", _TaskDonwloadToAGV.ExecutingTrajecory.GetTagList())},Destine={_TaskDonwloadToAGV.Destination},最後航向角度 ={_TaskDonwloadToAGV.ExecutingTrajecory.Last().Theta}");
                if (Vehicle.options.Simulation)
                {
                    TaskDownloadRequestResponse taskStateResponse = Vehicle.AgvSimulation.ExecuteTask(_TaskDonwloadToAGV).Result;
                    return taskStateResponse;
                }
                else
                {
                    try
                    {
                        TaskDownloadRequestResponse taskStateResponse = new TaskDownloadRequestResponse();

                        if (CommunicationProtocol == PROTOCOL.RESTFulAPI)
                            taskStateResponse = await Vehicle.AGVHttp.PostAsync<TaskDownloadRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", _TaskDonwloadToAGV);
                        else
                            taskStateResponse = Vehicle.TcpClientHandler.SendTaskMessage(_TaskDonwloadToAGV);
                        if (taskStateResponse.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
                        {
                            ExecutingTaskName = task.TaskName;
                            lastTaskDonwloadToAGV = _TaskDonwloadToAGV;
                        }

                        logger.Info($"Task Download To AGV:\n{_TaskDonwloadToAGV.ToJson()}");
                        logger.Info($"AGV Response Of Task Download:\n{taskStateResponse.ToJson()}");

                        return taskStateResponse;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex);
                        return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL };
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL };

            }
            finally
            {
                TaskExecuteSemaphoreSlim.Release();
            }

        }

        internal async Task EmergencyStop(string TaskName = "")
        {
            clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
            {
                ResetMode = RESET_MODE.ABORT,
                Task_Name = TaskName,
                TimeStamp = DateTime.Now,
            };
            SimpleRequestResponse taskStateResponse = new SimpleRequestResponse();
            if (CommunicationProtocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
            {
                taskStateResponse = await Vehicle.AGVHttp.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"/api/TaskDispatch/Cancel", reset_cmd);
            }
            else
            {
                taskStateResponse = Vehicle.TcpClientHandler.SendTaskCancelMessage(reset_cmd);
            }
        }

        /// <summary>
        /// 請求車輛結束當前任務並等待車輛回報ACTION_FINISH
        /// </summary>
        /// <returns></returns>
        internal async Task TaskCycleStop(string TaskName)
        {
            try
            {
                if (lastTaskDonwloadToAGV == null)
                    return;
                NotifyServiceHelper.WARNING($"{Vehicle.Name} Cycle Stop! ");

                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = RESET_MODE.CYCLE_STOP,
                    Task_Name = TaskName,
                    TimeStamp = DateTime.Now,
                };
                WaitACTIONFinishReportedMRE.Reset();
                SimpleRequestResponse taskStateResponse = new SimpleRequestResponse();
                if (CommunicationProtocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
                {
                    taskStateResponse = await Vehicle.AGVHttp.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"/api/TaskDispatch/Cancel", reset_cmd);
                }
                else
                {
                    taskStateResponse = Vehicle.TcpClientHandler.SendTaskCancelMessage(reset_cmd);
                }


                logger.Info($"Task Cycle Stop To AGV:\n{reset_cmd.ToJson()}");
                logger.Info($"AGV Response Of Task Cycle Stop:\n{taskStateResponse.ToJson()}");


                if (taskStateResponse.ReturnCode == RETURN_CODE.OK)
                {
                    logger.Trace("TaskCycleStop: Waiting for AGV report ACTION_FINISH");
                    bool actionFinishDone = WaitACTIONFinishReportedMRE.WaitOne(TimeSpan.FromMinutes(3));
                    //bool actionFinishDone = WaitACTIONFinishReportedMRE.WaitOne(TimeSpan.FromSeconds(3));

                    if (!actionFinishDone)
                    {
                        logger.Warn("Wait Action_Finish Timeout");
                    }
                    else
                    {
                        logger.Info("Vehicle Action_Finish Reported!");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                lastTaskDonwloadToAGV = null;
            }
        }

        internal async Task HandleVehicleTaskStatusFeedback(FeedbackData feedbackData)
        {
            try
            {
                logger.Info($"Vehicle Task Status Feedback: {feedbackData.ToJson()}");
                TASK_RUN_STATUS taskStatus = feedbackData.TaskStatus;
                string taskName = feedbackData.TaskName;
                if (taskStatus == TASK_RUN_STATUS.ACTION_FINISH)
                {
                    await Task.Delay(1);
                    WaitACTIONFinishReportedMRE.Set();

                    if (lastTaskDonwloadToAGV != null && lastTaskDonwloadToAGV.Destination == Vehicle.states.Last_Visited_Node)
                    {
                        lastTaskDonwloadToAGV = null;
                    }

                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }
    }
}

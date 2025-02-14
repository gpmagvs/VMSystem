using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using VMSystem.Extensions;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.Exceptions;
using VMSystem.VMS;
using WebSocketSharp;
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

        public string TrackingTaskSimpleName { get; private set; } = "";

        /// <summary>
        /// 最後一次下發給車輛的任務模型
        /// </summary>
        public clsTaskDownloadData lastTaskDonwloadToAGV { get; private set; } = null;

        private HttpHelper VehicleHttp => Vehicle.AGVHttp;
        private AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL CommunicationProtocol => Vehicle.options.Protocol;
        private NLog.Logger logger;

        internal ManualResetEvent WaitACTIONFinishReportedMRE = new ManualResetEvent(false);
        internal ManualResetEvent WaitNavigatingReportedMRE = new ManualResetEvent(false);
        internal ManualResetEvent WaitActionStartReportedMRE = new ManualResetEvent(false);
        private SemaphoreSlim TaskExecuteSemaphoreSlim = new SemaphoreSlim(1, 1);

        public event EventHandler<FeedbackData> OnActionFinishReported;
        public event EventHandler<FeedbackData> OnNavigatingReported;

        private int sequence = 0;

        public TaskExecuteHelper(clsAGV vehicle)
        {
            Vehicle = vehicle;
            logger = NLog.LogManager.GetLogger($"TaskExecuteHelper/{vehicle.Name}");
            logger.Trace("TaskExecuterHelper instance created");
        }
        public void Init()
        {
            ExecutingTaskName = "";
            TrackingTaskSimpleName = "";
            lastTaskDonwloadToAGV = null;
        }
        /// <summary>
        /// 任務下發給車輛
        /// </summary>
        /// <param name="task"></param>
        /// <param name="_TaskDonwloadToAGV"></param>
        /// <returns></returns>
        internal async Task<(TaskDownloadRequestResponse response, clsMapPoint[] trajectory)> TaskDownload(TaskBase task, clsTaskDownloadData _TaskDonwloadToAGV, bool IsRotateToAvoidAngleTask = false)
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

                if (_TaskDonwloadToAGV.Task_Name != ExecutingTaskName)
                    sequence = 0;

                if (_TaskDonwloadToAGV.ExecutingTrajecory.Length == 1 && _TaskDonwloadToAGV.ExecutingTrajecory.First().Point_ID != Vehicle.states.Last_Visited_Node)
                {
                    logger.Error($"Path send to AGV is incorrect!!!!");
                    return (new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION }, new clsMapPoint[0]);
                }


                bool allPathExist = CheckPathesExistOnMap(_TaskDonwloadToAGV.ExecutingTrajecory, out int fromTag, out int toTag);
                if (!allPathExist)
                {
                    throw new PathNotDefinedException($"Path From {fromTag} To {toTag} is not exist in route");
                }

                ExecutingTaskName = task.TaskName;

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

                //若路徑中包含閃避模式3的點位需確認 下一點的設備PORT是不是有AGV停駐
                bool changed = DynamicDisableDogeMode3(ref _TaskDonwloadToAGV);
                if (changed)
                {
                    NotifyServiceHelper.WARNING($"{Vehicle.Name} 導航路徑原有閃避模式3點位動態調整為0。(因設備內有其他車輛)");
                }

                int _sequence = int.Parse(sequence + "");
                string _newTaskSimplex = ExecutingTaskName + "_" + _sequence;
                _TaskDonwloadToAGV.Task_Sequence = _sequence;
                _TaskDonwloadToAGV.Task_Simplex = _newTaskSimplex;
                lastTaskDonwloadToAGV = _TaskDonwloadToAGV.Clone();
                TrackingTaskSimpleName = _newTaskSimplex;
                sequence += 1;
                bool IsAMVAGV = Vehicle.model == clsEnums.AGV_TYPE.INSPECTION_AGV;

                if (!IsAMVAGV && _TaskDonwloadToAGV.Action_Type == ACTION_TYPE.None && _TaskDonwloadToAGV.Trajectory.Length > 0)
                {
                    //check final angle. 
                    bool IsStopAtDestineTag = _TaskDonwloadToAGV.Trajectory.Last().Point_ID == _TaskDonwloadToAGV.Destination;
                    if (IsStopAtDestineTag && !IsRotateToAvoidAngleTask)
                    {
                        double angle = 0;
                        MapPoint navGoalStation = StaMap.GetPointByTagNumber(_TaskDonwloadToAGV.Destination);
                        MapPoint destStation = navGoalStation;
                        var taskSubStage = Vehicle.CurrentRunningTask().subStage;
                        if (taskSubStage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
                        {
                            MapPoint waitingPt = StaMap.GetPointByTagNumber(_TaskDonwloadToAGV.Trajectory.Last().Point_ID);
                            if (Vehicle.NavigationState.RegionControlState.NextToGoRegion.EnteryTagsWaitForwardAngles.TryGetValue(waitingPt.TagNumber, out double forwardAngle) && forwardAngle != 999)
                            {
                                angle = forwardAngle;
                            }
                            else
                            {
                                if (waitingPt.UseAvoidThetaWhenStopAtWaitingPointOfEntryRegion)
                                    angle = waitingPt.Direction_Avoid;
                                else
                                    angle = GetForwardThetaOfLastPath(_TaskDonwloadToAGV);
                            }
                        }
                        else if (taskSubStage == VehicleMovementStage.AvoidPath || taskSubStage == VehicleMovementStage.AvoidPath_Park)
                        {
                            if (taskSubStage == VehicleMovementStage.AvoidPath)
                                angle = destStation.Direction_Avoid;
                            else
                            {
                                destStation = StaMap.GetPointByTagNumber(Vehicle.NavigationState.AvoidActionState.AvoidPt.TagNumber);
                                angle = destStation.Direction;
                            }
                        }
                        else
                        {
                            if (task.OrderData.Action == ACTION_TYPE.None)
                            {
                                destStation = StaMap.GetPointByTagNumber(task.OrderData.To_Station_Tag);
                                angle = destStation.Direction;
                            }
                            else
                            {

                                if (task.Stage == VehicleMovementStage.Traveling_To_Destine)
                                {
                                    destStation = StaMap.GetPointByTagNumber(task.OrderData.To_Station_Tag);
                                }
                                else if (task.Stage == VehicleMovementStage.Traveling_To_Source)
                                {
                                    destStation = StaMap.GetPointByTagNumber(task.OrderData.From_Station_Tag);
                                }
                                angle = Tools.CalculationForwardAngle(navGoalStation, destStation);
                            }
                        }


                        _TaskDonwloadToAGV.Trajectory.Last().Theta = angle;
                    }
                    else if (_TaskDonwloadToAGV.Trajectory.Length > 1)
                    {
                        //停車角度為倒數第二個點往最後一個點的朝向角度
                        double angle = GetForwardThetaOfLastPath(_TaskDonwloadToAGV);
                        _TaskDonwloadToAGV.Trajectory.Last().Theta = angle;
                    }

                }

                logger.Info($"Trajectory prepared  send to AGV = {string.Join("->", _TaskDonwloadToAGV.ExecutingTrajecory.GetTagList())},Destine={_TaskDonwloadToAGV.Destination},最後航向角度 ={_TaskDonwloadToAGV.ExecutingTrajecory.Last().Theta}");

                try
                {
                    //一般走行須等待AGV上報 Navagating; otherwise 需等待AGV上報 Action_Start
                    ManualResetEvent waitReportMRE = _TaskDonwloadToAGV.Action_Type == ACTION_TYPE.None ? WaitNavigatingReportedMRE : WaitActionStartReportedMRE;
                    waitReportMRE.Reset();
                    _LogDownloadRequest(_TaskDonwloadToAGV);

                    TaskDownloadRequestResponse taskStateResponse = new TaskDownloadRequestResponse();

                    if (Vehicle.options.Simulation)
                        taskStateResponse = Vehicle.AgvSimulation.ExecuteTask(_TaskDonwloadToAGV).Result;
                    else
                    {
                        if (CommunicationProtocol == PROTOCOL.RESTFulAPI)
                            taskStateResponse = await Vehicle.AGVHttp.PostAsync<TaskDownloadRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", _TaskDonwloadToAGV);
                        else
                            taskStateResponse = Vehicle.TcpClientHandler.SendTaskMessage(_TaskDonwloadToAGV);
                    }

                    logger.Info($"Response Of Task Download:\n{taskStateResponse.ToJson()}");
                    if (taskStateResponse.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
                    {
                        logger.Trace($"Start wait AGV Report Navigation Status.)");
                        bool seted = waitReportMRE.WaitOne(TimeSpan.FromSeconds(3));
                        logger.Trace($"AGV Report Navigation Status => {(seted ? "Success" : "Timeout!")})");
                    }
                    else
                    {
                        //log 
                        logger.Warn($"Task is Reject by AGV! ReturnCode={taskStateResponse.ReturnCode}");
                    }

                    return (taskStateResponse, _TaskDonwloadToAGV.ExecutingTrajecory);
                }
                catch (HttpRequestException ex)
                {
                    //TODO 處理因網路異常造成API請求失敗的例外狀況
                    throw ex;
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                    return (new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL }, new clsMapPoint[0]);
                }

            }

            catch (HttpRequestException ex)
            {
                //TODO 處理因網路異常造成API請求失敗的例外狀況
                throw ex;
            }
            catch (PathNotDefinedException ex)
            {
                throw ex;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return (new TaskDownloadRequestResponse
                {
                    ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL
                }, new clsMapPoint[0]);

            }
            finally
            {
                TaskExecuteSemaphoreSlim.Release();
            }

            void _LogDownloadRequest(clsTaskDownloadData taskDownloadToAGV)
            {
                object logObject = new
                {
                    TaskName = taskDownloadToAGV.Task_Name,
                    TaskSimplex = taskDownloadToAGV.Task_Simplex,
                    TaskSequence = taskDownloadToAGV.Task_Sequence,
                    Action = taskDownloadToAGV.Action_Type,
                    Trajectory = string.Join("->", taskDownloadToAGV.ExecutingTrajecory.GetTagList()),
                    Destination = taskDownloadToAGV.Destination,
                    Heigh = taskDownloadToAGV.Height,
                    CST = taskDownloadToAGV.CST,
                    Station_Type = taskDownloadToAGV.Station_Type.ToString(),
                };
                logger.Info($"Task Download To AGV:\n{logObject.ToJson()}");
            }
        }

        private static double GetForwardThetaOfLastPath(clsTaskDownloadData _TaskDonwloadToAGV)
        {
            if (_TaskDonwloadToAGV.Trajectory.Length < 2)
                return _TaskDonwloadToAGV.Trajectory.Last().Theta;


            clsMapPoint ptLastSecond = _TaskDonwloadToAGV.Trajectory[_TaskDonwloadToAGV.Trajectory.Length - 2];
            clsMapPoint ptLast = _TaskDonwloadToAGV.Trajectory.Last();
            double angle = Tools.CalculationForwardAngle(new clsCoordination(ptLastSecond.X, ptLastSecond.Y, 0), new clsCoordination(ptLast.X, ptLast.Y, 0));
            return angle;
        }

        /// <summary>
        /// 檢查是否有不存在於圖資設定的路線
        /// </summary>
        /// <param name="executingTrajecory"></param>
        /// <returns></returns>
        private bool CheckPathesExistOnMap(clsMapPoint[] executingTrajecory, out int fromTag, out int toTag)
        {
            fromTag = 0;
            toTag = 0;
            if (executingTrajecory.Length < 2)
                return true;

            //0,1

            for (int i = 1; i < executingTrajecory.Length; i++)
            {
                int endTag = -1;
                int startTag = -1;
                try
                {
                    endTag = executingTrajecory[i].Point_ID;
                    startTag = executingTrajecory[i - 1].Point_ID;
                }
                catch (Exception)
                {
                    continue;
                }
                MapPoint startPT = StaMap.GetPointByTagNumber(startTag);
                MapPoint endPT = StaMap.GetPointByTagNumber(endTag);

                int startPtIndex = StaMap.GetIndexOfPoint(startPT);
                int endPtIndex = StaMap.GetIndexOfPoint(endPT);

                if (startPtIndex == -1 || endPtIndex == -1)
                    return false;

                bool pathExist = StaMap.Map.Segments.Any(path => path.StartPtIndex == startPtIndex && path.EndPtIndex == endPtIndex);
                if (!pathExist)
                {
                    fromTag = startTag;
                    toTag = endTag;

                    return false;
                }
            }
            return true;
        }

        private bool DynamicDisableDogeMode3(ref clsTaskDownloadData taskDonwloadToAGV)
        {
            bool hasAnyDodgeModeOfPointEuqal3 = taskDonwloadToAGV.Trajectory.Any(pt => pt.Control_Mode.Dodge == 3);
            if (!hasAnyDodgeModeOfPointEuqal3)
                return false;

            bool hasAfterDodgeMode3HasAgv = false;

            int indexOfDogeMode3Pt = taskDonwloadToAGV.Trajectory.ToList().FindIndex(pt => pt.Control_Mode.Dodge == 3);

            //after DogeMod3Pts 
            var _trajRef = taskDonwloadToAGV.Trajectory.ToList();
            var targetEqHasAgvPts = _trajRef.Where(pt => _trajRef.IndexOf(pt) > indexOfDogeMode3Pt)
                                            .Where(pt => IsTargetEqHasAGV(pt.Point_ID));
            hasAfterDodgeMode3HasAgv = targetEqHasAgvPts.Any();

            if (!hasAfterDodgeMode3HasAgv)
                return false;

            taskDonwloadToAGV.Trajectory[indexOfDogeMode3Pt].Control_Mode.Dodge = 0;
            return true;

            bool IsTargetEqHasAGV(int point_ID)
            {
                var otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Vehicle);
                MapPoint normalPt = StaMap.GetPointByTagNumber(point_ID);
                return normalPt.TargetWorkSTationsPoints().Any(pt => otherVehicles.Any(agv => agv.currentMapPoint.TagNumber == pt.TagNumber));
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
            if (Vehicle.simulationMode)
            {
                Vehicle.AgvSimulation.EMO();
                return;
            }
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

                if (lastTaskDonwloadToAGV.Action_Type != ACTION_TYPE.None)
                {
                    logger.Warn("TaskCycleStop: Not support for non-normal move task!");
                    NotifyServiceHelper.WARNING($"{Vehicle.Name} TaskCycleStop: Not support for non-normal move task!");
                    return;
                }
                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = RESET_MODE.CYCLE_STOP,
                    Task_Name = TaskName,
                    TimeStamp = DateTime.Now,
                };
                WaitACTIONFinishReportedMRE.Reset();
                SimpleRequestResponse taskStateResponse = new SimpleRequestResponse();
                bool isAGVIDLE = Vehicle.main_state != clsEnums.MAIN_STATUS.RUN;
                if (Vehicle.simulationMode)
                {
                    Vehicle.AgvSimulation.CancelTask(100);
                }
                else
                {
                    if (CommunicationProtocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
                    {
                        taskStateResponse = await Vehicle.AGVHttp.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"/api/TaskDispatch/Cancel", reset_cmd);
                    }
                    else
                    {
                        taskStateResponse = Vehicle.TcpClientHandler.SendTaskCancelMessage(reset_cmd);
                    }
                }
                logger.Info($"Task Cycle Stop To AGV:\n{reset_cmd.ToJson()}");
                logger.Info($"AGV Response Of Task Cycle Stop:\n{taskStateResponse.ToJson()}");


                if (taskStateResponse.ReturnCode == RETURN_CODE.OK)
                {
                    logger.Trace("TaskCycleStop: Waiting for AGV report ACTION_FINISH");

                    bool actionFinishDone = WaitACTIONFinishReportedMRE.WaitOne(isAGVIDLE ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(3));
                    if (!actionFinishDone)

                        logger.Warn("Wait Action_Finish Timeout" + $"{(isAGVIDLE ? "-對閒置中的AGV下發Cycle Stop但沒有收到Action Finish回報!" : "")}");
                    else
                        logger.Info("Vehicle Action_Finish Reported!");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                //lastTaskDonwloadToAGV = null;
                WaitACTIONFinishReportedMRE.Set();
            }
        }

        internal async Task<bool> HandleVehicleTaskStatusFeedback(FeedbackData feedbackData)
        {
            return await Task.Run(() =>
            {
                try
                {
                    TASK_RUN_STATUS taskStatus = feedbackData.TaskStatus;
                    logger.Info($"Vehicle Task Status Feedback ({taskStatus.ToString()}): {feedbackData.ToJson()}");
                    string taskName = feedbackData.TaskName;
                    string taskSimplex = feedbackData.TaskSimplex;
                    bool isFeedbackToCurrentTask = taskSimplex.IsNullOrEmpty() || TrackingTaskSimpleName.IsNullOrEmpty() ? true : taskSimplex == TrackingTaskSimpleName;
                    if (!isFeedbackToCurrentTask)
                    {
                        logger.Warn($"Feedback TaskSimplex={taskSimplex} is not match to TrackingTaskSimplex={TrackingTaskSimpleName}");
                        return true;
                    }

                    if (taskStatus == TASK_RUN_STATUS.ACTION_FINISH)
                    {
                        OnActionFinishReported?.Invoke(this, feedbackData);
                        WaitACTIONFinishReportedMRE.Set();
                    }
                    if (taskStatus == TASK_RUN_STATUS.ACTION_START)
                    {
                        WaitActionStartReportedMRE.Set();
                    }
                    if (taskStatus == TASK_RUN_STATUS.NAVIGATING)
                    {
                        OnNavigatingReported?.Invoke(this, feedbackData);
                        WaitNavigatingReportedMRE.Set();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                    return false;
                }
            });

        }
    }
}

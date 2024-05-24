using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using System.Diagnostics;
using System.Security.Claims;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions;
using static VMSystem.AGV.TaskDispatch.Tasks.MoveTask;
using static VMSystem.TrafficControl.TrafficControlCenter;

namespace VMSystem.AGV.TaskDispatch.Tasks
{

    public abstract class TaskBase : IDisposable
    {
        public delegate Task<clsMoveTaskEvent> BeforeMoveToNextGoalDelegate(clsMoveTaskEvent args);
        public static BeforeMoveToNextGoalDelegate BeforeMoveToNextGoalTaskDispatch;

        public delegate clsLeaveFromWorkStationConfirmEventArg BeforeLeaveFromWorkStationDelegate(clsLeaveFromWorkStationConfirmEventArg args);
        public static BeforeLeaveFromWorkStationDelegate BeforeLeaveFromWorkStation;

        public static event EventHandler<PathConflicRequest> OnPathConflicForSoloveRequest;

        protected Map CurrentMap => StaMap.Map;

        public ACTION_TYPE NextAction { get; set; } = ACTION_TYPE.NoAction;

        public MapPoint NextCheckPoint { get; set; } = new MapPoint();

        public IEnumerable<IAGV> OtherAGV => VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv);
        public TaskBase() { }
        public TaskBase(IAGV Agv, clsTaskDto orderData)
        {
            this.Agv = Agv;
            this.OrderData = orderData;
            TaskDonwloadToAGV.Action_Type = ActionType;
            TrafficWaitingState = new clsWaitingInfo(Agv);
        }
        public MapPoint InfrontOfWorkStationPoint = new MapPoint();
        public bool IsFinalAction { get; set; } = false;
        /// <summary>
        /// 當前任務的階段
        /// </summary>
        public abstract VehicleMovementStage Stage { get; set; }
        public TransferStage TransferStage { get; set; } = TransferStage.NO_Transfer;
        public abstract ACTION_TYPE ActionType { get; }
        /// <summary>
        /// 目的地Tag
        /// </summary>
        public int DestineTag { set; get; } = 0;
        public string TaskName { get; internal set; }
        public int TaskSequence { get; internal set; }
        public clsTaskDownloadData TaskDonwloadToAGV { protected set; get; } = new clsTaskDownloadData();
        public IAGV Agv { get; }
        public MapPoint AGVCurrentMapPoint => this.Agv.currentMapPoint;
        public clsTaskDto OrderData { get; }
        public string TaskSimple => TaskName + $"-{TaskSequence}";

        public Action<clsTaskDownloadData> OnTaskDownloadToAGV;

        public Action<ALARMS> OnTaskDownloadToAGVButAGVRejected;
        public clsMoveTaskEvent MoveTaskEvent { get; protected set; } = new clsMoveTaskEvent();
        public bool IsTaskCanceled { get; protected set; } = false;
        public List<int> PassedTags { get; set; } = new List<int>();
        public clsWaitingInfo TrafficWaitingState { set; get; } = new clsWaitingInfo();
        public IEnumerable<MapPoint> RealTimeOptimizePathSearchReuslt = new List<MapPoint>();
        public virtual bool IsAGVReachDestine
        {
            get
            {
                return Agv.states.Last_Visited_Node == TaskDonwloadToAGV.Destination;
            }
        }
        public bool cycleStopRequesting { get; protected set; }
        public async Task CycleStopRequestAsync()
        {
            cycleStopRequesting = true;
            await SendCancelRequestToAGV();
            cycleStopRequesting = false;
        }


        private List<IAGV> _WaitingForAGV = new List<IAGV>();
        public List<IAGV> WaitingForAGV
        {
            get => _WaitingForAGV;
            set
            {
                _WaitingForAGV = value;
                LOG.TRACE($"AGV=[ {string.Join(",", _WaitingForAGV.Select(agv => agv.Name))}] is Waiting For {this.Agv.Name}");
            }
        }
        protected MapPoint GetEntryPointsOfWorkStation(MapPoint _desintWorkStation, MapPoint agv_currentmappoint = null)
        {
            MapPoint _destine_point;
            var forbidsMapPoints = StaMap.GetNoStopPointsByAGVModel(this.Agv.model);
            var entryPoints = _desintWorkStation.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            var usablePoints = entryPoints.Where(pt => !forbidsMapPoints.Contains(pt));

            if (usablePoints.Any())
            {
                if (agv_currentmappoint != null)
                {
                    Dictionary<MapPoint, double> dict_distance_between_agvcurrentpoint_and_usablePoints = usablePoints.ToDictionary(x => x, x => double.MaxValue);
                    foreach (var p in dict_distance_between_agvcurrentpoint_and_usablePoints.Keys)
                    {
                        dict_distance_between_agvcurrentpoint_and_usablePoints[p] = Math.Sqrt(Math.Pow(agv_currentmappoint.X - p.X, 2) + Math.Pow(agv_currentmappoint.Y - p.Y, 2));
                    }
                    _destine_point = dict_distance_between_agvcurrentpoint_and_usablePoints.OrderByDescending(x => x.Value).LastOrDefault().Key;
                }
                else
                    _destine_point = usablePoints.FirstOrDefault();
            }
            else
                throw new NoPathForNavigatorException();
            return _destine_point;
        }
        internal virtual async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            try
            {

                MoveTaskEvent = new clsMoveTaskEvent();
                _TaskCancelTokenSource = new CancellationTokenSource();
                CreateTaskToAGV();
                await SendTaskToAGV();
                if (IsTaskCanceled)
                    return (false, ALARMS.Task_Canceled);
                return (true, ALARMS.NONE);
            }
            catch (TaskCanceledException ex)
            {
                TrafficWaitingState.SetDisplayMessage("任務取消中...");
                return (false, ALARMS.Task_Canceled);
            }
            catch (NoPathForNavigatorException ex)
            {
                return (false, ex.Alarm_Code);
            }
            catch (AGVRejectTaskException ex)
            {
                return (false, ex.Alarm_Code);
            }
            catch (Exception ex)
            {

                LOG.ERROR(ex.ToString(), ex);
                return (false, ALARMS.TASK_DOWNLOAD_TO_AGV_FAIL_SYSTEM_EXCEPTION);
            }
        }

        public virtual void CreateTaskToAGV()
        {
            TaskDonwloadToAGV.Task_Name = this.TaskName;
            TaskDonwloadToAGV.Task_Sequence = this.TaskSequence;
            TaskDonwloadToAGV.Task_Simplex = this.TaskSimple;
            TaskDonwloadToAGV.CST = new clsCST[]
            {
                 new clsCST(){
                    CST_ID = OrderData.Carrier_ID,
                    CST_Type = (CST_TYPE)this.OrderData.CST_TYPE
                 }
            };
        }

        public virtual async Task SendTaskToAGV()
        {

            if (IsTaskCanceled)
                return;
            //Console.WriteLine("Send To AGV: " + TaskDonwloadToAGV.ToJson());
            await SendTaskToAGV(TaskDonwloadToAGV);
        }
        public virtual async Task SendTaskToAGV(clsTaskDownloadData taskData)
        {
            //Console.WriteLine("Send To AGV: " + TaskDonwloadToAGV.ToJson());
            if (IsTaskCanceled)
                return;
            var agv_response = await _DispatchTaskToAGV(taskData);
            if (agv_response.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                throw new Exceptions.AGVRejectTaskException();
        }
        public abstract void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData);
        public abstract void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData);
        public static clsMapPoint MapPointToTaskPoint(MapPoint mapStation, int index = 0)
        {
            try
            {
                var Auto_Door = new clsAutoDoor
                {
                    Key_Name = mapStation.AutoDoor?.KeyName,
                    Key_Password = mapStation.AutoDoor?.KeyPassword,
                };

                var Control_Mode = new clsControlMode
                {
                    Dodge = (int)(mapStation.DodgeMode == null ? 0 : mapStation.DodgeMode),
                    Spin = (int)(mapStation.SpinMode == null ? 0 : mapStation.SpinMode)
                };

                return new clsMapPoint()
                {
                    index = index,
                    X = mapStation.X,
                    Y = mapStation.Y,
                    Auto_Door = Auto_Door,
                    Control_Mode = Control_Mode,
                    Laser = mapStation.LsrMode,
                    Map_Name = StaMap.Map.Name,
                    Point_ID = mapStation.TagNumber,
                    Speed = mapStation.Speed,
                    Theta = mapStation.Direction,
                };
            }
            catch (Exception ex)
            {
                throw ex;
            }

        }
        public List<int> FuturePlanNavigationTags = new List<int>();
        internal async Task<TaskDownloadRequestResponse> _DispatchTaskToAGV(clsTaskDownloadData _TaskDonwloadToAGV)
        {
            _TaskDonwloadToAGV.OrderInfo = new clsTaskDownloadData.clsOrderInfo
            {
                ActionName = OrderData.Action,
                NextAction = this.NextAction,
                SourceTag = OrderData.Action == ACTION_TYPE.Carry ? OrderData.From_Station_Tag : OrderData.To_Station_Tag,
                DestineTag = OrderData.To_Station_Tag,
                DestineName = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag).Graph.Display,
                SourceName = OrderData.Action == ACTION_TYPE.Carry ? StaMap.GetPointByTagNumber(OrderData.From_Station_Tag).Graph.Display : "",
                IsTransferTask = OrderData.Action == ACTION_TYPE.Carry,
                DestineSlot = int.Parse(OrderData.To_Slot),
                SourceSlot = int.Parse(OrderData.From_Slot)
            };

            if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts && ActionType == ACTION_TYPE.None)
            {
                TrafficWaitingState.SetStatusWaitingConflictPointRelease(null, "等待Parts系統回應站點註冊狀態");

                (bool confirm, string message, List<string> regions) parts_accept = (false, "", new List<string>());
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!parts_accept.confirm)
                {

                    if (stopwatch.Elapsed.TotalSeconds > 180)
                    {
                        TrafficWaitingState.SetStatusNoWaiting();
                        return new TaskDownloadRequestResponse
                        {
                            Message = parts_accept.message,
                            ReturnCode = TASK_DOWNLOAD_RETURN_CODES.Parts_System_Not_Allow_Point_Regist
                        };
                    }
                    parts_accept = await RegistToPartsSystem(_TaskDonwloadToAGV);
                    if (!parts_accept.confirm)
                    {
                        LOG.WARN($"Parts System Not Allow AMC AGV Regist Region- {string.Join(",", parts_accept.regions)}..Wait 1 sec and retry...");
                        await Task.Delay(1000);
                    }

                }
            }
            TrafficWaitingState.SetStatusNoWaiting();
            LOG.TRACE($"Trajectory send to AGV = {string.Join("->", _TaskDonwloadToAGV.ExecutingTrajecory.GetTagList())},Destine={_TaskDonwloadToAGV.Destination},最後航向角度 ={_TaskDonwloadToAGV.ExecutingTrajecory.Last().Theta}");
            if (Agv.options.Simulation)
            {
                TaskDownloadRequestResponse taskStateResponse = Agv.AgvSimulation.ExecuteTask(_TaskDonwloadToAGV).Result;
                return taskStateResponse;
            }
            else
            {
                try
                {
                    TaskDownloadRequestResponse taskStateResponse = new TaskDownloadRequestResponse();

                    if (Agv.options.Protocol == PROTOCOL.RESTFulAPI)
                        taskStateResponse = await Agv.AGVHttp.PostAsync<TaskDownloadRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", _TaskDonwloadToAGV);
                    else
                        taskStateResponse = Agv.TcpClientHandler.SendTaskMessage(_TaskDonwloadToAGV);

                    return taskStateResponse;
                }
                catch (Exception)
                {
                    return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL };
                }
            }
        }
        protected CancellationTokenSource _TaskCancelTokenSource = new CancellationTokenSource();
        protected bool disposedValue;

        public virtual void UpdateStateDisplayMessage(string msg)
        {
            TrafficWaitingState.SetDisplayMessage(msg);

        }
        public virtual void UpdateMoveStateMessage(string msg)
        {
            if (OrderData == null)
                return;
            string GetDestineDisplay()
            {
                int _destineTag = 0;
                bool isCarryOrderAndGoToSource = OrderData.Action == ACTION_TYPE.Carry && Stage == VehicleMovementStage.Traveling_To_Source;
                _destineTag = isCarryOrderAndGoToSource ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;
                return StaMap.GetStationNameByTag(_destineTag);
            }
            bool _isPathAvoiding = Stage == VehicleMovementStage.AvoidPath;
            TrafficWaitingState.SetDisplayMessage($"[{(_isPathAvoiding ? "避車" : OrderData.ActionName)}]-{(_isPathAvoiding ? "避讓點" : "終點")} : {GetDestineDisplay()}\r\n({msg})");
        }

        protected virtual async Task<(bool confirm, string message, List<string> regions)> RegistToPartsSystem(clsTaskDownloadData _TaskDonwloadToAGV)
        {
            var indexOfAgv = _TaskDonwloadToAGV.ExecutingTrajecory.ToList().FindIndex(pt => pt.Point_ID == Agv.currentMapPoint.TagNumber);
            var remainPoints = _TaskDonwloadToAGV.ExecutingTrajecory.Skip(indexOfAgv);
            var pointNames = remainPoints.Select(pt => StaMap.GetStationNameByTag(pt.Point_ID)).ToList();
            var pointTags = remainPoints.Select(pt => pt.Point_ID);
            FuturePlanNavigationTags = pointTags.ToList();
            var result = await TrafficControl.PartsAGVSHelper.RegistStationRequestToAGVS(pointNames);
            return (result.confirm, result.message, pointNames);
        }
        public virtual async void CancelTask()
        {
            _TaskCancelTokenSource.Cancel();
            IsTaskCanceled = true;
            this.Dispose();
            await SendCancelRequestToAGV();
            TrafficWaitingState.SetStatusNoWaiting();
        }

        internal async Task<SimpleRequestResponse> SendCancelRequestToAGV()
        {

            try
            {
                if (Agv == null)
                {
                    return new SimpleRequestResponse
                    {
                        ReturnCode = RETURN_CODE.OK
                    };
                }
                if (Agv.options.Simulation)
                {
                    Agv.AgvSimulation?.CancelTask();
                    return new SimpleRequestResponse
                    {
                        ReturnCode = RETURN_CODE.OK
                    };
                }

                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = RESET_MODE.CYCLE_STOP,
                    Task_Name = OrderData.TaskName,
                    TimeStamp = DateTime.Now,
                };
                SimpleRequestResponse taskStateResponse;
                if (Agv.options.Protocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
                {
                    taskStateResponse = await Agv.AGVHttp.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"/api/TaskDispatch/Cancel", reset_cmd);
                }
                else
                {
                    taskStateResponse = Agv.TcpClientHandler.SendTaskCancelMessage(reset_cmd);
                }
                LOG.WARN($"取消{Agv.Name}任務-AGV Response : Return Code :{taskStateResponse.ReturnCode},Message : {taskStateResponse.Message}");

                if (taskStateResponse.ReturnCode == RETURN_CODE.OK)
                {
                    await Task.Delay(1000);
                    while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                    {
                        await Task.Delay(1000);
                        if (IsTaskCanceled || disposedValue)
                            break;
                    }

                }
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

        internal void Replan(List<int> tags)
        {
            clsTaskDownloadData _replanTask = TaskDonwloadToAGV.Clone();
            _replanTask.Trajectory = tags.Select(tag => StaMap.GetPointByTagNumber(tag)).Select(mapPt => MapPointToTaskPoint(mapPt)).ToArray();
            SendTaskToAGV(_replanTask);
        }

        public virtual void ActionFinishInvoke()
        {
            FuturePlanNavigationTags.Clear();
            TrafficWaitingState.SetStatusNoWaiting();
        }

        public void PathConflicSolveRequestInvoke(PathConflicRequest request)
        {
            TaskBase.OnPathConflicForSoloveRequest?.Invoke(this, request);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 處置受控狀態 (受控物件)
                }

                // TODO: 釋出非受控資源 (非受控物件) 並覆寫完成項
                // TODO: 將大型欄位設為 Null
                disposedValue = true;
            }
        }

        // // TODO: 僅有當 'Dispose(bool disposing)' 具有會釋出非受控資源的程式碼時，才覆寫完成項
        // ~TaskBase()
        // {
        //     // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        internal virtual void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            Task.Run(async () =>
            {
                await Task.Delay(300);
                PassedTags.Add(Agv.currentMapPoint.TagNumber);
            });
        }
    }

    public class clsLeaveFromWorkStationConfirmEventArg : EventArgs
    {
        public enum LEAVE_WORKSTATION_ACTION
        {
            OK,
            WAIT,
            CANCEL
        }
        public IAGV? Agv;
        public int GoalTag;
        public LEAVE_WORKSTATION_ACTION ActionConfirm = LEAVE_WORKSTATION_ACTION.OK;
        public ManualResetEvent WaitSignal = new ManualResetEvent(false);

        public string Message { get; internal set; }
    }
    public class clsMoveTaskEvent : EventArgs
    {

        public enum GOTO_NEXT_GOAL_CONFIRM_RESULT
        {
            ACCEPTED_GOTO_NEXT_GOAL,
            WAIT_IN_CURRENT_LOCATION,
            REPLAN,
            CANCEL,
            GO_TO_CHECK_POINT_AND_WAIT,
            WAIT_TRAFFIC_CONTROL
        }

        public clsAGVRequests AGVRequestState { get; set; } = new clsAGVRequests();
        public clsTrafficControlResponse TrafficResponse { get; set; } = new clsTrafficControlResponse();

        public clsMoveTaskEvent()
        {

        }
        public clsMoveTaskEvent(IAGV agv, IEnumerable<int> tagsOfTrajectory, List<MapPoint> nextSequenceTaskTrajectoryTagList, bool isTrafficeControlTask)
        {
            this.AGVRequestState.Agv = agv;
            this.AGVRequestState.OptimizedToDestineTrajectoryTagList = tagsOfTrajectory.ToList();
            this.AGVRequestState.NextSequenceTaskTrajectory = nextSequenceTaskTrajectoryTagList?.ToList();
            this.AGVRequestState.IsTrafficeControlTask = isTrafficeControlTask;
            this.TrafficResponse.TrafficWaitingState = new clsWaitingInfo(agv);
        }
        public clsMoveTaskEvent(IAGV agv, List<List<MapPoint>> _taskSequenceList, IEnumerable<int> tagsOfTrajectory, List<MapPoint> nextSequenceTaskTrajectoryTagList, bool isTrafficeControlTask)
        {
            this.AGVRequestState.Agv = agv;
            this.AGVRequestState.OptimizedToDestineTrajectoryTagList = tagsOfTrajectory.ToList();
            this.AGVRequestState.SequenceTaskTrajectoryList = _taskSequenceList;
            this.AGVRequestState.NextSequenceTaskTrajectory = nextSequenceTaskTrajectoryTagList.ToList();
            this.AGVRequestState.IsTrafficeControlTask = isTrafficeControlTask;
            this.TrafficResponse.TrafficWaitingState = new clsWaitingInfo(agv);
        }


        public class clsAGVRequests
        {
            public IAGV? Agv;
            public int NextExecuteSeqPathIndex = 0;
            public List<int> OptimizedToDestineTrajectoryTagList { get; internal set; } = new List<int>();
            public List<List<MapPoint>> SequenceTaskTrajectoryList { get; internal set; }
            public List<MapPoint> NextSequenceTaskTrajectory { get; internal set; } = new List<MapPoint>();
            public List<int> RemainTagList
            {
                get
                {
                    if (OptimizedToDestineTrajectoryTagList.Count == 0)
                        return new List<int>();
                    int agv_location_pt_index = OptimizedToDestineTrajectoryTagList.IndexOf(Agv.states.Last_Visited_Node);
                    return OptimizedToDestineTrajectoryTagList.Skip(agv_location_pt_index).ToList();
                }
            }
            public List<int> NextSequenceTaskRemainTagList
            {
                get
                {
                    if (NextSequenceTaskTrajectory == null || NextSequenceTaskTrajectory.Count == 0)
                        return new List<int>();
                    int agv_location_pt_index = NextSequenceTaskTrajectory.GetTagCollection().ToList().IndexOf(Agv.states.Last_Visited_Node);
                    return NextSequenceTaskTrajectory.Skip(agv_location_pt_index + 1).GetTagCollection().ToList();
                }
            }
            public bool IsTrafficeControlTask { get; internal set; }

        }
        public class clsTrafficControlResponse
        {
            public GOTO_NEXT_GOAL_CONFIRM_RESULT ConfirmResult = GOTO_NEXT_GOAL_CONFIRM_RESULT.ACCEPTED_GOTO_NEXT_GOAL;
            public int AccpetExecuteSeqPathIndex = 0;
            public ManualResetEvent Wait_Traffic_Control_Finish_ResetEvent { get; set; } = new ManualResetEvent(false);
            public clsMapPoint[] NewTrajectory { get; set; } = new clsMapPoint[0];
            public clsWaitingInfo TrafficWaitingState { get; set; } = new clsWaitingInfo();
            public List<int> BlockedTags { get; internal set; }
            public List<IAGV> YieldWayAGVList { get; internal set; }
        }
    }

}

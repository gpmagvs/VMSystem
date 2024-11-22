using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Material;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Notify;
using AGVSystemCommonNet6.Microservices.MCS;
using Newtonsoft.Json;
using NLog;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using System.Security.Claims;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions;
using static SQLite.SQLite3;
using static VMSystem.AGV.TaskDispatch.Tasks.MoveTask;
using static VMSystem.TrafficControl.TrafficControlCenter;
using AGVSystemCommonNet6.Configuration;
using VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.Extensions;

namespace VMSystem.AGV.TaskDispatch.Tasks
{

    public abstract class TaskBase : clsTaskDatabaseWriteableAbstract, IDisposable
    {
        public delegate Task<clsMoveTaskEvent> BeforeMoveToNextGoalDelegate(clsMoveTaskEvent args);
        public static BeforeMoveToNextGoalDelegate BeforeMoveToNextGoalTaskDispatch;

        public delegate clsLeaveFromWorkStationConfirmEventArg BeforeLeaveFromWorkStationDelegate(clsLeaveFromWorkStationConfirmEventArg args);
        public static BeforeLeaveFromWorkStationDelegate BeforeLeaveFromWorkStation;

        public static event EventHandler<PathConflicRequest> OnPathConflicForSoloveRequest;

        public event EventHandler OnTaskDone;

        protected Map CurrentMap => StaMap.Map;

        public ACTION_TYPE NextAction { get; set; } = ACTION_TYPE.NoAction;

        public MapPoint NextCheckPoint { get; set; } = new MapPoint();

        public IEnumerable<IAGV> OtherAGV => VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv);

        public Logger logger;

        public TaskBase parentTaskBase { get; set; } = null;

        internal ManualResetEvent TaskExecutePauseMRE = new ManualResetEvent(true);

        public TaskDiagnosis taskdiagnosisTool { get; set; } = new TaskDiagnosis();

        protected bool AgvStatusDownFlag = false;

        internal AGV.TaskDispatch.OrderHandler.OrderTransferSpace.OrderTransfer? OrderTransfer { get; set; } = null;
        protected virtual void HandleAGVStatusDown(object? sender, EventArgs e)
        {
            AgvStatusDownFlag = true;
            Agv.OnAGVStatusDown -= HandleAGVStatusDown;
            Task.Run(() =>
            {
                var agvAlarmsDescription = string.Join(",", Agv.states.Alarm_Code.Where(alarm => alarm.Alarm_Category != 0).Select(alarm => alarm.FullDescription));
                Agv.CancelTaskAsync(this.OrderData.TaskName, agvAlarmsDescription);
                _WaitAGVTaskDoneMRE.Set();
            });
        }
        public TaskBase(IAGV Agv, clsTaskDto orderData) { }
        public TaskBase() : base() { }
        public TaskBase(IAGV Agv, clsTaskDto orderData, AGVSDbContext agvsDb, SemaphoreSlim taskTbModifyLock) : base(agvsDb, taskTbModifyLock)
        {
            this.Agv = Agv;
            this.OrderData = orderData;
            this.TaskName = orderData.TaskName;
            TaskDonwloadToAGV.Action_Type = ActionType;
            TrafficWaitingState = new clsWaitingInfo(Agv);
            logger = LogManager.GetLogger("TaskDispatch");
        }
        public MapPoint InfrontOfWorkStationPoint = new MapPoint();
        public bool IsFinalAction { get; set; } = false;
        /// <summary>
        /// 當前任務的階段
        /// </summary>
        public abstract VehicleMovementStage Stage { get; set; }
        public VehicleMovementStage subStage { get; protected set; } = VehicleMovementStage.Not_Start_Yet;

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
        public clsTaskDto OrderData { get; } = new clsTaskDto();
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
                logger.Trace($"AGV=[ {string.Join(",", _WaitingForAGV.Select(agv => agv.Name))}] is Waiting For {this.Agv.Name}");
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
        internal virtual async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            try
            {
                MoveTaskEvent = new clsMoveTaskEvent();
                _TaskCancelTokenSource = new CancellationTokenSource();
                CreateTaskToAGV();
                _ = OrderTransfer?.WatchStart();
                await SendTaskToAGV();
                if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                    return (false, ALARMS.AGV_STATUS_DOWN, "AGV STATUS DOWN");
                else if (IsTaskCanceled)
                    return (false, ALARMS.Task_Canceled, "TASK CANCELED");

                return (true, ALARMS.NONE, "");
            }
            catch (TaskCanceledException ex)
            {
                TrafficWaitingState.SetDisplayMessage("任務取消中...");
                return (false, ALARMS.Task_Canceled, "TASK CANCELED");
            }
            catch (NoPathForNavigatorException ex)
            {
                return (false, ex.Alarm_Code, "NO PATH NAVIGATION");
            }
            catch (AGVRejectTaskException ex)
            {
                return (false, ex.Alarm_Code, "AGV REJECT TASK");
            }
            catch (VMSExceptionAbstract ex)
            {
                throw ex;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return (false, ALARMS.TASK_DOWNLOAD_TO_AGV_FAIL_SYSTEM_EXCEPTION, "TASK_DOWNLOAD_TO_AGV_FAIL_SYSTEM_EXCEPTION");
            }
            finally
            {
                if (OrderTransfer != null)
                {
                    OrderTransfer.Abort();
                    if (OrderTransfer.State == OrderHandler.OrderTransferSpace.OrderTransfer.STATES.ORDER_TRANSFERIED)
                    {
                        OrderTransfer.OrderDone();
                    }
                }
            }
        }
        protected virtual bool IsTaskExecutable()
        {
            if (IsTaskCanceled ||
                Agv.main_state == clsEnums.MAIN_STATUS.DOWN ||
                Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING ||
                Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                return false;
            return true;
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
                return; (TaskDownloadRequestResponse agv_response, clsMapPoint[] _trajectory) = await _DispatchTaskToAGV(taskData);
            if (agv_response.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                throw new Exceptions.AGVRejectTaskException(agv_response.ReturnCode);
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
        internal async Task<(TaskDownloadRequestResponse, clsMapPoint[] trajectory)> _DispatchTaskToAGV(clsTaskDownloadData _TaskDonwloadToAGV)
        {

            int retryCnt = 0;
            int retryMaxLimit = 5;
            Exception _exceptionHappening = null;
            while (retryCnt < retryMaxLimit)
            {
                try
                {
                    logger.Warn($"嘗試下載任務給AGV..({retryCnt})");
                    return await _DownloadTaskInvoke(_TaskDonwloadToAGV);
                }
                catch (HttpRequestException ex)
                {
                    _exceptionHappening = ex;
                    retryCnt++;
                    logger.Warn($"嘗試下載任務給AGV過程中發生 [HttpRequestException] 例外({ex.Message})，等待一秒後重新嘗試...({retryCnt})");
                    await Task.Delay(1000);
                    continue;
                }
                catch (PathNotDefinedException ex) //若是因為有未存在路徑的例外須直接拋出，要執行cycle stop + replan.
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    _exceptionHappening = ex;
                    retryCnt++;
                    logger.Warn($"嘗試下載任務給AGV過程中發生例外({ex.Message})，等待一秒後重新嘗試...({retryCnt})");
                    await Task.Delay(1000);
                    continue;
                }
                finally
                {
                    if (_exceptionHappening == null)
                    {
                        logger.Info($"_DispatchTaskToAGV Success!");
                    }
                }
            }
            // if code run here=> retry count reach max limit
            return (new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION, Message = _exceptionHappening?.Message }, new clsMapPoint[0]);

            async Task<(TaskDownloadRequestResponse, clsMapPoint[] trajectory)> _DownloadTaskInvoke(clsTaskDownloadData _TaskDonwloadToAGV)
            {
                if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts && ActionType == ACTION_TYPE.None)
                {
                    TrafficWaitingState.SetStatusWaitingConflictPointRelease(null, "等待Parts系統回應站點註冊狀態");

                    (bool confirm, string message, List<string> regions) parts_accept = (false, "", new List<string>());
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    while (!parts_accept.confirm)
                    {
                        if (IsTaskCanceled)
                        {
                            throw new TaskCanceledException();
                        }
                        if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                        {
                            throw new AGVStatusDownException();
                        }
                        if (stopwatch.Elapsed.TotalSeconds > 180)
                        {
                            TrafficWaitingState.SetStatusNoWaiting();
                            return (new TaskDownloadRequestResponse
                            {
                                Message = parts_accept.message,
                                ReturnCode = TASK_DOWNLOAD_RETURN_CODES.Parts_System_Not_Allow_Point_Regist
                            }, new clsMapPoint[0]);
                        }
                        parts_accept = await RegistToPartsSystem(_TaskDonwloadToAGV);
                        if (!parts_accept.confirm)
                        {
                            logger.Warn($"Parts System Not Allow AMC AGV Regist Region- {string.Join(",", parts_accept.regions)}..Wait 1 sec and retry...");
                            await Task.Delay(1000);
                        }

                    }
                }
                TrafficWaitingState.SetStatusNoWaiting();
                return await Agv.TaskExecuter.TaskDownload(this, _TaskDonwloadToAGV);
            }
        }
        public CancellationTokenSource _TaskCancelTokenSource = new CancellationTokenSource();
        public CancellationTokenSource TrajectoryRecordCancelTokenSource = new CancellationTokenSource();
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
            try
            {
                _TaskCancelTokenSource.Cancel();
                IsTaskCanceled = true;
                this.Dispose();
                await SendCancelRequestToAGV();
                NavigationResume(isResumeByWaitTimeout: false);
            }
            catch (Exception ex)
            {
                logger?.Error(ex);

            }
        }

        internal async Task<SimpleRequestResponse> SendCancelRequestToAGV()
        {
            try
            {
                if (Agv == null)
                {
                    return new SimpleRequestResponse();
                }

                await Agv?.TaskExecuter?.TaskCycleStop(this.OrderData?.TaskName);
                return new SimpleRequestResponse();
            }
            catch (Exception ex)
            {
                logger?.Error(ex);
                throw ex;
            }
        }

        internal void Replan(List<int> tags)
        {
            clsTaskDownloadData _replanTask = TaskDonwloadToAGV.Clone();
            _replanTask.Trajectory = tags.Select(tag => StaMap.GetPointByTagNumber(tag)).Select(mapPt => MapPointToTaskPoint(mapPt)).ToArray();
            SendTaskToAGV(_replanTask);
        }
        protected ManualResetEvent _WaitAGVTaskDoneMRE = new ManualResetEvent(false);

        protected virtual async Task WaitAGVTaskDone()
        {
            _WaitAGVTaskDoneMRE.Reset();

            void ActionFinishFeedbackHandler(object sender, FeedbackData feedbackData)
            {
                if (IsThisTaskDone(feedbackData))
                {
                    Agv.TaskExecuter.OnActionFinishReported -= ActionFinishFeedbackHandler;
                    _WaitAGVTaskDoneMRE.Set();
                }
            };
            void TaskCancelHandler(object sender, string taskName)
            {
                Agv.OnTaskCancel -= TaskCancelHandler;
                _WaitAGVTaskDoneMRE.Set();
            }
            Agv.OnTaskCancel += TaskCancelHandler;
            Agv.TaskExecuter.OnActionFinishReported += ActionFinishFeedbackHandler;
            _WaitAGVTaskDoneMRE.WaitOne();
        }

        public virtual bool IsThisTaskDone(FeedbackData feedbackData)
        {
            return feedbackData.TaskSimplex == Agv.TaskExecuter.TrackingTaskSimpleName;
        }

        public virtual (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) ActionFinishInvoke()
        {
            (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) result = (true, null, ALARMS.NONE, "");

            try
            {
                MCSCIMService.TaskStatus taskstate = MCSCIMService.TaskStatus.None;
                if (this.Stage < VehicleMovementStage.Traveling_To_Source)
                {
                    taskstate = MCSCIMService.TaskStatus.wait_to_source;
                }
                else if (this.Stage == VehicleMovementStage.Traveling_To_Source)
                {
                    taskstate = MCSCIMService.TaskStatus.at_source_wait_in;
                }
                else if (this.Stage == VehicleMovementStage.WorkingAtSource)
                {
                    taskstate = MCSCIMService.TaskStatus.wait_to_dest;
                }
                else if (this.Stage == VehicleMovementStage.Traveling_To_Destine)
                {
                    taskstate = MCSCIMService.TaskStatus.at_destination_wait_in;
                }
                else if (this.Stage == VehicleMovementStage.WorkingAtDestination)
                {
                    taskstate = MCSCIMService.TaskStatus.wait_to_complete;
                }
                else
                    taskstate = MCSCIMService.TaskStatus.ignore;

                Task<(bool confirm, string message)> v = AGVSSerivces.TaskReporter((OrderData, taskstate)); // 各段任務結束上報
                v.Wait();
                if (v.Result.confirm == false)
                    logger.Warn($"{v.Result.message}");
            }
            catch (Exception ex)
            {
                logger.Warn($"{ex.Message}");
            }
            try
            {
                if (this.Stage == VehicleMovementStage.WorkingAtSource) //取貨
                {
                    MaterialInstallStatus cargoinstall = (Agv.states.Cargo_Status == 0) ? MaterialInstallStatus.NG : MaterialInstallStatus.OK;
                    MaterialType cargotype = (Agv.states.CargoType == 1) ? MaterialType.Frame : MaterialType.Tray;
                    MaterialIDStatus idmatch = (OrderData.Carrier_ID == "" || OrderData.Carrier_ID == null || OrderData.Carrier_ID == Agv.states.CSTID[0]) ? MaterialIDStatus.OK : MaterialIDStatus.NG;
                    MaterialManager.CreateMaterialInfo(OrderData.Carrier_ID, ActualID: Agv.states.CSTID[0], SourceStation: OrderData.From_Station, TargetStation: Agv.Name,
                        TaskSource: OrderData.From_Station, TaskTarget: OrderData.To_Station, installStatus: cargoinstall, IDStatus: idmatch, materialType: cargotype, materialCondition: MaterialCondition.Transfering);
                    if (cargoinstall != MaterialInstallStatus.OK)
                    {
                        logger.Fatal("[ActionFinishInvoke] cargo not install");
                        CancelTask();
                        result.errorMsg = "AGV載貨可能發生騎Tray/框(Cargo maybe not mounted on AGV)";
                        result.alarmCode = ALARMS.UNLOAD_BUT_AGV_NO_CARGO_MOUNTED;
                        result.continuetask = false;
                    }
                    else if (idmatch == MaterialIDStatus.NG && AGVSConfigulator.SysConfigs.EQManagementConfigs.TransferToNGPortWhenCarrierIDMissmatch)
                    {
                        try
                        {
                            (bool confirm, string message, object ngPortObject) = AGVSSerivces.GetNGPort().GetAwaiter().GetResult();
                            clsTransferMaterial ngport = null;
                            try
                            {
                                ngport = JsonConvert.DeserializeObject<clsTransferMaterial>(ngPortObject.ToString());
                            }
                            catch (Exception ex)
                            {
                                logger.Error(ex);
                            }
                            if (ngport != null)
                            {
                                OrderData.To_Station = ngport.TargetTag.ToString();
                                OrderData.To_Slot = ngport.TargetRow.ToString();
                                result.task = OrderData;
                                result.continuetask = true;
                            }
                            else
                            {
                                logger.Fatal("[ActionFinishInvoke] No NG port can use, task fail");
                                CancelTask();
                                result.errorMsg = "No NG port can use";
                                result.alarmCode = ALARMS.No_NG_Port_Can_Be_Used;
                                result.continuetask = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Fatal($"[ActionFinishInvoke] get No NG port with exception: {ex.Message}, task fail");
                            result.alarmCode = ALARMS.SYSTEM_ERROR;
                            CancelTask();
                            result.errorMsg = $"No NG port can use:{ex.Message}";
                            result.continuetask = false;
                        }
                    }
                    MaterialManager.CreateMaterialInfo(OrderData.Carrier_ID, ActualID: Agv.states.CSTID[0], SourceStation: OrderData.From_Station, TargetStation: Agv.Name,
                        TaskSource: OrderData.From_Station, TaskTarget: OrderData.To_Station, installStatus: cargoinstall, IDStatus: idmatch, materialType: cargotype, materialCondition: MaterialCondition.Transfering);
                }
                else if (this.Stage == VehicleMovementStage.WorkingAtDestination)
                {
                    MaterialType cargotype = (Agv.states.CargoType == 1) ? MaterialType.Frame : MaterialType.Tray;
                    MaterialManager.CreateMaterialInfo(OrderData.Carrier_ID, materialType: cargotype, materialCondition: MaterialCondition.Done);
                    clsStationInfoManager.UpdateStationInfo(OrderData, cargotype, OrderData.Actual_Carrier_ID);
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"{ex.Message}");
                result.alarmCode = ALARMS.SYSTEM_ERROR;
                result.errorMsg = $"Code Error:{ex.Message}";
                result.continuetask = false;
            }
            FuturePlanNavigationTags.Clear();
            TrafficWaitingState.SetStatusNoWaiting();
            return result;
        }

        protected void InvokeTaskDoneEvent()
        {
            OnTaskDone?.Invoke(this, EventArgs.Empty);
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
        internal bool NavigationPausing { get; set; } = false;
        internal string PauseNavigationReason { get; set; } = "";
        /// <summary>
        /// 暫停當前導航
        /// </summary>
        internal async void NavigationPause(bool isPauseWhenNavigating, string descritption, MapPoint blockedMapPoint = null)
        {
            if (blockedMapPoint != null)
                Agv.NavigationState.LastWaitingForPassableTimeoutPt = blockedMapPoint;
            PauseNavigationReason = descritption;
            NavigationPausing = true;
            NotifyServiceHelper.WARNING($"{Agv.Name} 導航動作暫停中 ({descritption})");
            TaskExecutePauseMRE.Reset();

            if (isPauseWhenNavigating)
            {
                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                {
                    await Task.Delay(1000);
                }
                logger.Trace(PauseNavigationReason);
                UpdateStateDisplayMessage(PauseNavigationReason);
                Agv.NavigationState.ResetNavigationPoints();
                StaMap.UnRegistPointsOfAGVRegisted(Agv);
                var waitCanPassPtPassableCancle = new CancellationTokenSource();
                if (blockedMapPoint.TagNumber != DestineTag)
                {
                    int _waitTimeout = TrafficControlCenter.TrafficControlParameters.Navigation.TimeoutWhenWaitPtPassableByEqPartReplacing;
                    logger.Info($"{Agv.Name} 開始等待 {blockedMapPoint.TagNumber}可通行({_waitTimeout}s)[導航途中CYCLE STOP]");
                    TimeSpan ts = TimeSpan.FromSeconds(_waitTimeout);
                    waitCanPassPtPassableCancle.CancelAfter(ts);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            while (!waitCanPassPtPassableCancle.IsCancellationRequested)
                            {

                                await Task.Delay(1000, waitCanPassPtPassableCancle.Token);
                                if (!NavigationPausing)
                                    return;
                                UpdateStateDisplayMessage(PauseNavigationReason + $"({sw.Elapsed.ToString(@"mm\:ss")}/{ts.ToString(@"mm\:ss")})");

                            }
                        }
                        catch (TaskCanceledException ex)
                        {

                        }
                        finally
                        {
                            if (NavigationPausing)//取消等待的當下，導航還是暫停=>表示超時等待
                            {
                                logger.Info($"{Agv.Name} Wait {blockedMapPoint.TagNumber} 可通行已逾時({_waitTimeout}s),開始繞行![導航途中CYCLE STOP]");
                                Agv.NavigationState.LastWaitingForPassableTimeoutPt = blockedMapPoint;
                                NavigationResume(isResumeByWaitTimeout: true);
                            }
                            else
                            {
                                //因為導航繼續而結束等待
                            }
                        }
                    });
                }
            }

        }
        /// <summary>
        /// 暫停當前導航
        /// </summary>
        internal void NavigationResume(bool isResumeByWaitTimeout)
        {
            if (!isResumeByWaitTimeout && Agv != null)
            {
                Agv.NavigationState.LastWaitingForPassableTimeoutPt = null;
                NotifyServiceHelper.INFO($"{Agv.Name} 導航動作已繼續({(isResumeByWaitTimeout ? "因等待超時" : "解除路徑封閉")})");
            }
            TaskExecutePauseMRE.Set();
            PauseNavigationReason = "";
            NavigationPausing = false;
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

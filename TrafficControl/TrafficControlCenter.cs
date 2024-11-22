using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using Newtonsoft.Json;
using NLog;
using System.Collections.Concurrent;
using System.Data;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using VMSystem.Extensions;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.TrafficControl.Solvers;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.AGV.TaskDispatch.Tasks.clsLeaveFromWorkStationConfirmEventArg;

namespace VMSystem.TrafficControl
{
    public partial class TrafficControlCenter
    {
        public static List<clsWaitingInfo> AGVWaitingQueue = new List<clsWaitingInfo>();
        internal static SemaphoreSlim _leaveWorkStaitonReqSemaphore = new SemaphoreSlim(1, 1);
        internal static clsTrafficControlParameters TrafficControlParameters { get; set; } = new clsTrafficControlParameters();
        private static FileSystemWatcher TrafficControlParametersChangedWatcher;

        static Logger logger = LogManager.GetCurrentClassLogger();

        internal static void Initialize()
        {
            LoadTrafficControlParameters();
            SystemModes.OnRunModeON += HandleRunModeOn;
            TaskBase.BeforeMoveToNextGoalTaskDispatch += ProcessTaskRequest;
            TaskBase.OnPathConflicForSoloveRequest += HandleOnPathConflicForSoloveRequest;
            OrderHandlerBase.OnBufferOrderStarted += OrderHandlerBase_OnBufferOrderStarted;
            LoadUnloadTask.OnReleaseEntryPointRequesting += LoadUnloadTask_OnReleaseEntryPointRequesting;
            //TaskBase.BeforeMoveToNextGoalTaskDispatch += HandleAgvGoToNextGoalTaskSend;
            StaMap.OnTagUnregisted += StaMap_OnTagUnregisted;
            Task.Run(() => TrafficStateCollectorWorker());
            logger.Debug("TrafficControlCenter Initialized");
        }


        private static void LoadTrafficControlParameters()
        {
            string _fullFileName = Path.Combine(AGVSConfigulator.ConfigsFilesFolder, "TrafficControlParams.json");
            if (File.Exists(_fullFileName))
            {
                TrafficControlParameters = JsonConvert.DeserializeObject<clsTrafficControlParameters>(File.ReadAllText(_fullFileName));
            }

            File.WriteAllText(_fullFileName, JsonConvert.SerializeObject(TrafficControlParameters, Formatting.Indented));
            TrafficControlParametersChangedWatcher = new FileSystemWatcher(AGVSConfigulator.ConfigsFilesFolder, "TrafficControlParams.json");
            TrafficControlParametersChangedWatcher.Changed += TrafficControlParametersChangedWatcher_Changed;
            TrafficControlParametersChangedWatcher.EnableRaisingEvents = true;
            logger.Debug("TrafficControlCenter Load TrafficControl Parameters done");
        }

        private static void TrafficControlParametersChangedWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            logger.Debug($"TrafficControlParametersChangedWatcher_Changed event triggered.({e.ChangeType})");
            try
            {
                TrafficControlParametersChangedWatcher.EnableRaisingEvents = false;
                string tempFile = Path.Combine(Path.GetTempPath(), $"{DateTime.Now.Ticks}_{Path.GetFileName(e.FullPath)}");

                // 添加重試機制
                const int maxRetries = 3;
                const int delayMs = 100;

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        File.Copy(e.FullPath, tempFile, true);
                        break; // 如果複製成功，跳出循環
                    }
                    catch (IOException ioEx)
                    {
                        if (i == maxRetries - 1) // 最後一次嘗試
                        {
                            logger.Error(ioEx, $"無法複製檔案 {e.FullPath}，已重試 {maxRetries} 次");
                            throw;
                        }
                        logger.Warn($"複製檔案時發生 IO 錯誤，正在重試 ({i + 1}/{maxRetries})");
                        Thread.Sleep(delayMs); // 等待一段時間後重試
                    }
                }
                var _newTrafficControlParameters = JsonConvert.DeserializeObject<clsTrafficControlParameters>(File.ReadAllText(tempFile));
                if (_newTrafficControlParameters != null)
                {
                    TrafficControlParameters = _newTrafficControlParameters;
                    logger.Debug($"Update TrafficControlParameters DTO :\r\n {TrafficControlParameters.ToJson()}");
                }
                // Ensure temp file is deleted even if deserialization fails
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                    logger.Debug($"Delete temp file :{tempFile}");

                }

            }
            catch (Exception ex)
            {
                logger.Error(ex, $"處理{e.FullPath}檔案修改變化的過程中發生例外:{ex.Message}");
            }
            finally
            {
                TrafficControlParametersChangedWatcher.EnableRaisingEvents = true;
            }
        }

        public static clsDynamicTrafficState DynamicTrafficState { get; set; } = new clsDynamicTrafficState();
        public static AGVSDbContext AGVDbContext { get; internal set; }

        private static async void HandleRunModeOn()
        {
            var needGoToChargeAgvList = VMSManager.AllAGV.Where(agv => agv.currentMapPoint != null)
                                                         .Where(agv => !agv.currentMapPoint.IsCharge &&
                                                                        agv.main_state == MAIN_STATUS.IDLE &&
                                                                        agv.states.Cargo_Status == 0 &&
                                                                        agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.NO_ORDER)
                                                                        .ToList();
            foreach (var agv in needGoToChargeAgvList)
            {
                if (agv.states.Cargo_Status != 0)
                {
                    await AlarmManagerCenter.AddAlarmAsync(ALARMS.Cannot_Auto_Parking_When_AGV_Has_Cargo, level: ALARM_LEVEL.WARNING, Equipment_Name: agv.Name, location: agv.currentMapPoint.Name);
                    return;
                }
                agv.taskDispatchModule.OrderExecuteState = clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTABLE;
                //using (TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper())
                //{
                //    TaskDBHelper.Add(new clsTaskDto
                //    {
                //        Action = ACTION_TYPE.Charge,
                //        TaskName = $"Charge_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                //        DispatcherName = "VMS",
                //        DesignatedAGVName = agv.Name,
                //        RecieveTime = DateTime.Now,
                //    });
                //}
                await Task.Delay(1000);
            }
        }

        private static void StaMap_OnTagUnregisted(object? sender, int unregisted_tag)
        {
            var waiting_info_matched = AGVWaitingQueue.FirstOrDefault(waiting_info => waiting_info.WaitingPoint.TagNumber == unregisted_tag);
            if (waiting_info_matched != null)
            {
                waiting_info_matched.AllowMoveResumeResetEvent.Set();
            }
        }
        private static DateTime _lastForbiddenNotifyMsgSendTime = DateTime.MinValue;

        public static async Task<bool> AGVLeaveWorkStationRequest(string AGVName, int eQTag)
        {
            logger.Trace($"Get {AGVName} Leave from work station(Tag-{eQTag}) request");
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            ACTION_TYPE currentAction = agv.CurrentRunningTask().ActionType;
            clsMapPoint[] agvCurrentHomingTraj = agv.CurrentRunningTask().TaskDonwloadToAGV.Homing_Trajectory;
            bool _isDischargeOrUnParkActionOfAGV = currentAction == ACTION_TYPE.Discharge || currentAction == ACTION_TYPE.Unpark;

            int entryTag = _isDischargeOrUnParkActionOfAGV ? agvCurrentHomingTraj.Last().Point_ID :
                                                             agvCurrentHomingTraj.First().Point_ID;
            var EntryPointOfEQ = StaMap.GetPointByTagNumber(entryTag);

            clsLeaveFromWorkStationConfirmEventArg response = await TrafficControlCenter.HandleAgvLeaveFromWorkstationRequest(new clsLeaveFromWorkStationConfirmEventArg()
            {
                Agv = agv,
                GoalTag = EntryPointOfEQ.TagNumber,
            });

            var trafficState = response.Agv.taskDispatchModule.OrderHandler.RunningTask.TrafficWaitingState;
            trafficState.SetStatusWaitingConflictPointRelease(new List<int> { EntryPointOfEQ.TagNumber }, "退出設備確認中...");
            bool allowLeve = response.ActionConfirm == LEAVE_WORKSTATION_ACTION.OK;
            if (!allowLeve)
            {
                logger.Trace($"{AGVName} Leave from work station(Tag-{eQTag}) request NOT ALLOWED.... Reason:{response.Message}");
                trafficState.SetStatusWaitingConflictPointRelease(new List<int> { EntryPointOfEQ.TagNumber }, $"退出設備-等待主幹道可通行..\r\n({response.Message})");
            }
            else
            {
                logger.Trace($"{AGVName} Leave from work station(Tag-{eQTag}) request ALLOWED!");
                trafficState.SetStatusWaitingConflictPointRelease(new List<int> { EntryPointOfEQ.TagNumber }, "退出允許!");
                await Task.Delay(200);
                trafficState.SetStatusNoWaiting();

            }
            return allowLeve;
        }

        internal static async Task<clsLeaveFromWorkStationConfirmEventArg> HandleAgvLeaveFromWorkstationRequest(clsLeaveFromWorkStationConfirmEventArg args)
        {

            IAGV _RaiseReqAGV = args.Agv;
            Task _CycleStopTaskOfOtherVehicle = null;
            try
            {
                await _leaveWorkStaitonReqSemaphore.WaitAsync();
                var otherAGVList = VMSManager.AllAGV.FilterOutAGVFromCollection(_RaiseReqAGV);
                MapPoint goalPoint = StaMap.GetPointByTagNumber(args.GoalTag);
                bool isLeaveFromChargeStation = _RaiseReqAGV.currentMapPoint.IsCharge;
                string _waitMessage = "";

                clsConflicDetectResultWrapper _result = new(DETECTION_RESULT.NG, "");
                if (isLeaveFromChargeStation)
                {
                    LeaveChargeStationConflicDetection _LeaveChargeDetector = new LeaveChargeStationConflicDetection(goalPoint, _RaiseReqAGV.states.Coordination.Theta, _RaiseReqAGV);
                    _result = _LeaveChargeDetector.Detect();
                    _waitMessage += _result.Message;
                }
                else
                {
                    LeaveWorkstationConflicDetection _LeaveMainEQDetector = new LeaveWorkstationConflicDetection(goalPoint, _RaiseReqAGV.states.Coordination.Theta, _RaiseReqAGV);
                    _result = _LeaveMainEQDetector.Detect();
                }

                //addiction check .
                //await RegionManager.StartWaitToEntryRegion(_RaiseReqAGV, goalPoint.GetRegion(), _RaiseReqAGV.CurrentRunningTask()._TaskCancelTokenSource.Token);

                EnterWorkStationDetection enterWorkStationDetection = new(goalPoint, _RaiseReqAGV.states.Coordination.Theta, _RaiseReqAGV);
                clsConflicDetectResultWrapper workstationLeaveAddictionCheckResult = enterWorkStationDetection.Detect();
                _waitMessage += workstationLeaveAddictionCheckResult.Result == DETECTION_RESULT.OK ? "" : "\r\n" + workstationLeaveAddictionCheckResult.Message;




                LeaveParkStationConflicDetection _LeaveParkDetector = new LeaveParkStationConflicDetection(goalPoint, _RaiseReqAGV.states.Coordination.Theta, _RaiseReqAGV);
                clsConflicDetectResultWrapper _parkResult = _LeaveParkDetector.Detect();
                _waitMessage += _parkResult.Result == DETECTION_RESULT.OK ? "" : "\r\n" + _parkResult.Message;


                var entryPointOfWorkStation = StaMap.GetPointByTagNumber(args.GoalTag);
                bool _isAllowLeaveByDeadLockDetection = _RaiseReqAGV.NavigationState.LeaveWorkStationHighPriority;
                bool _isNeedWait = _isAllowLeaveByDeadLockDetection ? false :
                                    (_result.Result == DETECTION_RESULT.NG || workstationLeaveAddictionCheckResult.Result == DETECTION_RESULT.NG) || _parkResult.Result == DETECTION_RESULT.NG;




                CONFLIC_STATUS_CODE conflicStatus = _result.ConflicStatusCode;
                _RaiseReqAGV.NavigationState.IsWaitingForLeaveWorkStation = _isNeedWait;

                if (_isNeedWait)
                {
                    if ((DateTime.Now - _lastForbiddenNotifyMsgSendTime).TotalSeconds > 1)
                    {
                        NotifyServiceHelper.WARNING($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}尚不允許:{_waitMessage}");
                        _lastForbiddenNotifyMsgSendTime = DateTime.Now;
                    }
                    List<IAGV> conflicVehicles = _result.ConflicToAGVList;
                    args.WaitSignal.Reset();
                    args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.WAIT;
                    args.Message = _waitMessage;
                }
                else
                {
                    await Task.Delay(1000);
                    if (!StaMap.RegistPoint(_RaiseReqAGV.Name, entryPointOfWorkStation, out string _erMsg))
                    {
                        NotifyServiceHelper.WARNING($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}尚不允許!:{_erMsg}");
                        _isNeedWait = true;
                        args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.WAIT;
                        args.Message = _erMsg;
                    }
                    else
                    {

                        //NotifyServiceHelper.SUCCESS($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}已許可!");
                        var radius = _RaiseReqAGV.AGVRotaionGeometry.RotationRadius;
                        var forbidPoints = StaMap.Map.Points.Values.Where(pt => pt.CalculateDistance(entryPointOfWorkStation) <= radius);
                        List<MapPoint> _navingPointsForbid = new List<MapPoint>();
                        _navingPointsForbid.Add(_RaiseReqAGV.currentMapPoint);
                        _navingPointsForbid.Add(entryPointOfWorkStation);

                        //計算AGV若抵達進入點，車體長度延伸1.2倍後，車體涵蓋範圍會與那些路徑重疊
                        List<MapRectangle> agvConverRegions = Tools.GetPathRegionsWithRectangle(_navingPointsForbid, _RaiseReqAGV.options.VehicleWidth / 100.0, _RaiseReqAGV.options.VehicleLength * 1.2 / 100.0);

                        List<MapPath> intersectionPathes = StaMap.Map.Segments.Where(seg => IsIntersection(seg)).ToList();
                        bool IsIntersection(MapPath seg)
                        {
                            MapPoint _startPt = StaMap.GetPointByIndex(seg.StartPtIndex);
                            MapPoint _endPt = StaMap.GetPointByIndex(seg.EndPtIndex);
                            if (_startPt.StationType != STATION_TYPE.Normal || _startPt.StationType != STATION_TYPE.Normal)
                                return false;
                            var _pathRectangle = Tools.GetPathRegionsWithRectangle(new List<MapPoint>() { _startPt, _endPt }, _RaiseReqAGV.options.VehicleWidth / 100.0, _RaiseReqAGV.options.VehicleLength / 100.0);
                            return _pathRectangle.Any(reg => reg.IsIntersectionTo(agvConverRegions.First()));
                        }
                        List<int> addictionRegistedTags = new List<int>();
                        foreach (MapPath _path in intersectionPathes)
                        {
                            MapPoint _startPt = StaMap.GetPointByIndex(_path.StartPtIndex);
                            MapPoint _endPt = StaMap.GetPointByIndex(_path.EndPtIndex);
                            if (_startPt.StationType == STATION_TYPE.Normal)
                            {

                                bool registedResult1 = StaMap.RegistPoint(_RaiseReqAGV.Name, _startPt, out _);
                                if (registedResult1)
                                    addictionRegistedTags.Add(_startPt.TagNumber);
                            }
                            if (_endPt.StationType == STATION_TYPE.Normal)
                            {
                                bool registedResult2 = StaMap.RegistPoint(_RaiseReqAGV.Name, _endPt, out _);
                                if (registedResult2)
                                    addictionRegistedTags.Add(_endPt.TagNumber);
                            }
                        }

                        addictionRegistedTags = addictionRegistedTags.Distinct().OrderBy(t => t).ToList();
                        NotifyServiceHelper.SUCCESS($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}已許可!");
                        logger.Info($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}已許可! 額外註冊點:{string.Join(",", addictionRegistedTags)}");
                        _RaiseReqAGV.NavigationState.UpdateNavigationPoints(_navingPointsForbid);
                        (_RaiseReqAGV.CurrentRunningTask() as LoadUnloadTask)?.UpdateEQActionMessageDisplay();
                        args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;
                    }

                }

                bool _isAcceptAction = args.ActionConfirm == clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;

                if (!_isAcceptAction)
                {
                    _RaiseReqAGV.NavigationState.ResetNavigationPoints();
                }
                else if (_CycleStopTaskOfOtherVehicle != null)
                {
                    _CycleStopTaskOfOtherVehicle.Start();
                }

                return args;
            }
            catch (TaskCanceledException)
            {
                args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.CANCEL;
                return args;
            }
            finally
            {
                _leaveWorkStaitonReqSemaphore.Release();
            }

            #region region method

            bool IsCycleStopAllow(IAGV vehicle, MapPoint _entryPointOfWorkStation)
            {
                //判斷未經過進入點
                var pathRemain = vehicle.NavigationState.NextNavigtionPoints.DistinctBy(pt => pt.TagNumber).ToList();

                bool isAlreadyPassEntryPt = pathRemain.Skip(1).All(pt => pt.TagNumber != _entryPointOfWorkStation.TagNumber);

                //if (isAlreadyPassEntryPt)
                //    return false;

                bool IsDistanceFarwayEntryPoint = _entryPointOfWorkStation.CalculateDistance(vehicle.states.Coordination) >= 5.0;
                int indexOfPtOfCurrent = pathRemain.FindIndex(pt => pt.TagNumber == vehicle.currentMapPoint.TagNumber); //當前點的index 0.1.2.3
                int indexofCycleStopPoint = indexOfPtOfCurrent + 1;
                if (indexofCycleStopPoint == pathRemain.Count)
                    return false;
                var cycleStopPoint = pathRemain[indexofCycleStopPoint];
                double distanceToEntryPointOfCycleStopPt = cycleStopPoint.CalculateDistance(_entryPointOfWorkStation);
                return IsDistanceFarwayEntryPoint && distanceToEntryPointOfCycleStopPt > 2.0;
            }
            #endregion
        }


        public static List<TrafficControlCommander> TrafficEventCommanders = new List<TrafficControlCommander>();


        private static async void TrafficStateCollectorWorker()
        {
            while (true)
            {
                await Task.Delay(10);
                try
                {
                    DynamicTrafficState.AGVTrafficStates = VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv =>
                        new clsAGVTrafficState
                        {
                            AGVName = agv.Name,
                            CurrentPosition = StaMap.GetPointByTagNumber(agv.states.Last_Visited_Node),
                            AGVStatus = agv.main_state,
                            IsOnline = agv.online_state == ONLINE_STATE.ONLINE,
                            PlanningNavTrajectory = agv.main_state != MAIN_STATUS.RUN ? new List<MapPoint>() : agv.PlanningNavigationMapPoints.ToList(),
                        }
                    );
                    DynamicTrafficState.RegistedPoints = StaMap.RegistDictionary;

                }
                catch (Exception ex)
                {

                }

            }

        }


        private static void OrderHandlerBase_OnBufferOrderStarted(object? sender, OrderHandlerBase.BufferOrderState bufferOrderState)
        {
            AvoidVehicleSolver? makeVehicleLeavingFromSourcePortSolver = _TryCreateAvoidSolver(bufferOrderState.bufferFrom);
            AvoidVehicleSolver? makeVehicleLeavingFromDestinePortSolver = _TryCreateAvoidSolver(bufferOrderState.bufferTo);

            if (makeVehicleLeavingFromDestinePortSolver == null && makeVehicleLeavingFromSourcePortSolver == null)
            {
                bufferOrderState.message = "Source station and Destine station is reachable!";
                bufferOrderState.returnCode = ALARMS.NONE;

                return;
            }
            List<Task<ALARMS>> solverTasks = new List<Task<ALARMS>>();


            solverTasks.Add(_WaitSolverDoneTask(makeVehicleLeavingFromDestinePortSolver));

            if (bufferOrderState.bufferFrom != null && bufferOrderState.bufferFrom.TagNumber != bufferOrderState.bufferTo.TagNumber)
                solverTasks.Add(_WaitSolverDoneTask(makeVehicleLeavingFromSourcePortSolver));

            Task.WaitAll(solverTasks.ToArray());

            var failureSolver = solverTasks.FirstOrDefault(task => task.Result != ALARMS.NONE);
            bufferOrderState.returnCode = failureSolver == null ? ALARMS.NONE : failureSolver.Result;

            async Task<ALARMS> _WaitSolverDoneTask(AvoidVehicleSolver solver)
            {
                if (solver == null)
                    return ALARMS.NONE;
                return await solver.Solve();
            }

            AvoidVehicleSolver? _TryCreateAvoidSolver(MapPoint pt)
            {
                if (pt == null)
                    return null;
                IAGV orderOwnerAGV = bufferOrderState.orderBase.Agv;
                IAGV? agvAtPoint = VMSManager.AllAGV.FilterOutAGVFromCollection(orderOwnerAGV)
                                                    .FirstOrDefault(vehicle => vehicle.currentMapPoint.TagNumber == pt.TagNumber);
                if (agvAtPoint == null)
                    return null;

                return new AvoidVehicleSolver(agvAtPoint, ACTION_TYPE.Park, AGVDbContext);
            }
        }


        private static void LoadUnloadTask_OnReleaseEntryPointRequesting(object? sender, LoadUnloadTask.ReleaseEntryPtRequest releasePtRequest)
        {
            //GOAL:若有其他AGV任務的目標為此TAG 則不允許解註冊,
            int portTagNumber = releasePtRequest.Agv.currentMapPoint.TagNumber;
            List<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(releasePtRequest.Agv).ToList();

            var vehicleCurrentStationType = releasePtRequest.Agv.currentMapPoint.StationType;

            bool isAGVAtRack = vehicleCurrentStationType == STATION_TYPE.Buffer || vehicleCurrentStationType == STATION_TYPE.Buffer_EQ || vehicleCurrentStationType == STATION_TYPE.Charge_Buffer;

            bool releaseForbidden = otherVehicles.Any(vehicle => _IsGoToReleaseTagOrEntryPort(vehicle) || _IsGoToNearPortOfCurrentRack(vehicle));
            if (releaseForbidden)
                releasePtRequest.Message = $"有車輛目的地為 Tag {portTagNumber} 或 Tag {releasePtRequest.EntryPoint.TagNumber}";
            releasePtRequest.Accept = !releaseForbidden;

            //車輛任務是否為鄰近Port
            bool _IsGoToNearPortOfCurrentRack(IAGV _agv)
            {

                //功能開關
                if (TrafficControlCenter.TrafficControlParameters.Experimental.NearRackPortParkable)
                    return false;

                if (!isAGVAtRack || _agv == null)
                    return false;
                if (_agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                    return false;
                var _runningTask = _agv.CurrentRunningTask();
                if (_runningTask == null)
                    return false;

                int _tag = -1;
                if (_runningTask.Stage == VehicleMovementStage.Traveling_To_Source || _runningTask.Stage == VehicleMovementStage.WorkingAtSource)
                    _tag = _runningTask.OrderData.From_Station_Tag;
                else
                    _tag = _runningTask.OrderData.To_Station_Tag;
                MapPoint requestVehicleMapPt = StaMap.GetPointByTagNumber(portTagNumber);
                var entryPointsOfRequestVehicle = requestVehicleMapPt.TargetNormalPoints();
                var pointsOfNearByEntryPoints = entryPointsOfRequestVehicle.SelectMany(entry => entry.TargetNormalPoints());

                //判斷目的地是鄰近設備的進入點
                bool parkAtNearPortEntry = pointsOfNearByEntryPoints.Any(pt => pt.TagNumber == _tag);
                if (parkAtNearPortEntry)
                    return true;

                //判斷目的地是鄰近設備
                bool destineWorkStationNearByPort = StaMap.GetPointByTagNumber(_tag).TargetNormalPoints().Any(pt => pointsOfNearByEntryPoints.GetTagCollection().Contains(pt.TagNumber));
                if (destineWorkStationNearByPort)
                    return true;

                return true;
            }

            //
            bool _IsGoToReleaseTagOrEntryPort(IAGV _agv)
            {
                if (_agv == null)
                    return false;
                var _runningTask = _agv.CurrentRunningTask();
                if (_runningTask == null)
                    return false;
                bool _agvDestineIsReleaseTag = _runningTask.ActionType == ACTION_TYPE.None && _runningTask.DestineTag == releasePtRequest.EntryPoint.TagNumber;
                bool _agvNextActionIsEntrySamePort = _runningTask.NextAction != ACTION_TYPE.None && _GetNextActionDestineTag(_runningTask) == portTagNumber;
                return _agvDestineIsReleaseTag || _agvNextActionIsEntrySamePort;
            }

            int _GetNextActionDestineTag(TaskBase _runningTask)
            {
                VehicleMovementStage actionStage = _runningTask.Stage;
                //當前任務動作是移動至來源設備
                if (actionStage == VehicleMovementStage.Traveling_To_Source)
                    return _runningTask.OrderData.From_Station_Tag;
                //當前任務動作是移動至目的地設備或正在來源設備工作中
                else if (actionStage == VehicleMovementStage.Traveling_To_Destine || actionStage == VehicleMovementStage.WorkingAtSource)
                    return _runningTask.OrderData.To_Station_Tag;
                else
                    return -1;
            }

        }
    }
}

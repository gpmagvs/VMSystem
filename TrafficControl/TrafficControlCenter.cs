using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Data;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.TrafficControl.Solvers;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.TrafficControl
{
    public partial class TrafficControlCenter
    {
        public static List<clsWaitingInfo> AGVWaitingQueue = new List<clsWaitingInfo>();
        private static SemaphoreSlim _leaveWorkStaitonReqSemaphore = new SemaphoreSlim(1, 1);
        internal static clsTrafficControlParameters TrafficControlParameters { get; set; } = new clsTrafficControlParameters();
        private static FileSystemWatcher TrafficControlParametersChangedWatcher;

        internal static void Initialize()
        {
            LoadTrafficControlParameters();
            SystemModes.OnRunModeON += HandleRunModeOn;
            TaskBase.BeforeMoveToNextGoalTaskDispatch += ProcessTaskRequest;
            TaskBase.OnPathConflicForSoloveRequest += HandleOnPathConflicForSoloveRequest;
            //TaskBase.BeforeMoveToNextGoalTaskDispatch += HandleAgvGoToNextGoalTaskSend;
            StaMap.OnTagUnregisted += StaMap_OnTagUnregisted;
            Task.Run(() => TrafficStateCollectorWorker());
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
        }

        private static void TrafficControlParametersChangedWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            string _tempFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(e.FullPath));
            File.Copy(e.FullPath, _tempFile, true);
            var _newTrafficControlParameters = JsonConvert.DeserializeObject<clsTrafficControlParameters>(File.ReadAllText(_tempFile));
            if (_newTrafficControlParameters != null)
            {
                TrafficControlParameters = _newTrafficControlParameters;
                LOG.TRACE($"TrafficControlParameters changed:\r\n{TrafficControlParameters.ToJson()}");
            }
            File.Delete(_tempFile);
        }

        public static clsDynamicTrafficState DynamicTrafficState { get; set; } = new clsDynamicTrafficState();
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

        internal static async Task<clsLeaveFromWorkStationConfirmEventArg> HandleAgvLeaveFromWorkstationRequest(clsLeaveFromWorkStationConfirmEventArg args)
        {

            IAGV _RaiseReqAGV = args.Agv;
            Task _CycleStopTaskOfOtherVehicle = null;
            var otherAGVList = VMSManager.AllAGV.FilterOutAGVFromCollection(_RaiseReqAGV);

            try
            {
                await Task.Delay(100);
                //await _leaveWorkStaitonReqSemaphore.WaitAsync();
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
                    NotifyServiceHelper.WARNING($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}尚不允許:{_waitMessage}");
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

                        NotifyServiceHelper.SUCCESS($"AGV {_RaiseReqAGV.Name} 請求退出至 Tag-{args.GoalTag}已許可!");
                        var radius = _RaiseReqAGV.AGVRotaionGeometry.RotationRadius;
                        var forbidPoints = StaMap.Map.Points.Values.Where(pt => pt.CalculateDistance(entryPointOfWorkStation) <= radius);
                        List<MapPoint> _navingPointsForbid = new List<MapPoint>();
                        _navingPointsForbid.AddRange(new List<MapPoint> { _RaiseReqAGV.currentMapPoint, entryPointOfWorkStation });
                        _RaiseReqAGV.NavigationState.UpdateNavigationPoints(_navingPointsForbid);
                        (_RaiseReqAGV.CurrentRunningTask() as LoadUnloadTask)?.UpdateEQActionMessageDisplay();
                        args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;
                    }

                }

                bool _isAcceptAction = args.ActionConfirm == clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;

                if (!_isAcceptAction)
                {
                    _RaiseReqAGV.NavigationState.ResetNavigationPoints();
                    await Task.Delay(200);
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
                //_leaveWorkStaitonReqSemaphore.Release();
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


    }
}

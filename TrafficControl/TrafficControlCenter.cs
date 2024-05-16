using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using System.Collections.Concurrent;
using System.Data;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl.Solvers;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.TrafficControl
{
    public partial class TrafficControlCenter
    {
        public static ConcurrentQueue<clsTrafficInterLockSolver> InterLockTrafficSituations { get; set; } = new ConcurrentQueue<clsTrafficInterLockSolver>();
        public static List<clsWaitingInfo> AGVWaitingQueue = new List<clsWaitingInfo>();

        private static SemaphoreSlim _leaveWorkStaitonReqSemaphore = new SemaphoreSlim(1, 1);

        internal static void Initialize()
        {
            SystemModes.OnRunModeON += HandleRunModeOn;
            clsWaitingInfo.OnAGVWaitingStatusChanged += ClsWaitingInfo_OnAGVWaitingStatusChanged;
            clsSubTask.OnPathClosedByAGVImpactDetecting += ClsSubTask_OnPathClosedByAGVImpactDetecting;
            TaskBase.BeforeMoveToNextGoalTaskDispatch += ProcessTaskRequest;
            TaskBase.OnPathConflicForSoloveRequest += HandleOnPathConflicForSoloveRequest;
            //TaskBase.BeforeMoveToNextGoalTaskDispatch += HandleAgvGoToNextGoalTaskSend;
            StaMap.OnTagUnregisted += StaMap_OnTagUnregisted;
            Task.Run(() => TrafficStateCollectorWorker());
            Task.Run(() => TrafficInterLockSolveWorker());
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
            await _leaveWorkStaitonReqSemaphore.WaitAsync();

            Task _CycleStopTaskOfOtherVehicle = null;
            var otherAGVList = VMSManager.AllAGV.FilterOutAGVFromCollection(args.Agv);
            try
            {
                var entryPointOfWorkStation = StaMap.GetPointByTagNumber(args.GoalTag);
                bool _isNeedWait = IsNeedWait(args.GoalTag, args.Agv, otherAGVList, out bool isTagRegisted, out bool isTagBlocked, out bool isInterference, out bool isInterfercenWhenRotation, out List<IAGV> conflicVehicles);
                if (_isNeedWait)
                {

                    if (isInterference && conflicVehicles.Any())//干涉
                    {
                        bool _isOtherConflicVehicleFar = conflicVehicles.All(_vehicle => IsCycleStopAllow(_vehicle, entryPointOfWorkStation));
                        if (_isOtherConflicVehicleFar)
                        {

                            _CycleStopTaskOfOtherVehicle = new Task(async () =>
                            {
                                foreach (var conflic_vehicle in conflicVehicles)
                                {
                                    await conflic_vehicle.CurrentRunningTask().CycleStopRequestAsync();
                                }
                            });
                            args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;
                        }
                        else
                        {
                            args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.WAIT;
                            args.Message = CreateWaitingInfoDisplayMessage(args, isTagRegisted, isTagBlocked, isInterfercenWhenRotation);
                        }
                    }
                    else
                    {
                        args.WaitSignal.Reset();
                        clsWaitingInfo TrafficWaittingInfo = args.Agv.taskDispatchModule.OrderHandler.RunningTask.TrafficWaitingState;
                        args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.WAIT;
                        args.Message = CreateWaitingInfoDisplayMessage(args, isTagRegisted, isTagBlocked, isInterfercenWhenRotation);
                    }
                }
                else
                {

                    var radius = args.Agv.AGVRotaionGeometry.RotationRadius;
                    var forbidPoints = StaMap.Map.Points.Values.Where(pt => pt.CalculateDistance(entryPointOfWorkStation) <= radius);
                    List<MapPoint> _navingPointsForbid = new List<MapPoint>();
                    _navingPointsForbid.AddRange(new List<MapPoint> { args.Agv.currentMapPoint, entryPointOfWorkStation });
                    _navingPointsForbid.AddRange(forbidPoints.SelectMany(pt => new List<MapPoint>() { entryPointOfWorkStation, pt }));
                    _navingPointsForbid = _navingPointsForbid.Distinct().ToList();
                    args.Agv.NavigationState.UpdateNavigationPoints(_navingPointsForbid);
                    args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;
                }
                bool _isAcceptAction = args.ActionConfirm == clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;

                args.Agv.NavigationState.IsWaitingForLeaveWorkStation = _isAcceptAction;

                if (!_isAcceptAction)
                {
                    args.Agv.NavigationState.ResetNavigationPoints();
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
                _leaveWorkStaitonReqSemaphore.Release();
            }

            #region region method
            string CreateWaitingInfoDisplayMessage(clsLeaveFromWorkStationConfirmEventArg args, bool isTagRegisted, bool isTagBlocked, bool isInterfercenWhenRotation)
            {
                // 定义基础消息
                string baseMessage = "等待通行";

                // 根据条件选择具体的提示信息
                if (isInterfercenWhenRotation)
                {
                    return $"{baseMessage}-抵達終點後旋轉可能會與其他車輛干涉";
                }
                else if (isTagRegisted)
                {
                    return $"{baseMessage}-{args.GoalTag} 被其他車輛註冊";
                }
                else if (isTagBlocked)
                {
                    return $"{baseMessage}-{args.GoalTag}有AGV無法通行";
                }
                else
                {
                    return $"{baseMessage}-與其他車輛干涉";
                }
            }
            bool IsPathInterference(int goal, IEnumerable<IAGV> _otherAGVList)
            {
                MapPoint[] path = new MapPoint[2] { args.Agv.currentMapPoint, StaMap.GetPointByTagNumber(goal) };
                return TrafficControl.Tools.CalculatePathInterference(path, args.Agv, _otherAGVList, true);
            }

            bool IsDestineBlocked()
            {
                return otherAGVList.Any(agv => agv.states.Last_Visited_Node == args.GoalTag);
            }
            bool IsDestineRegisted(int goal, string AgvName)
            {
                if (!StaMap.RegistDictionary.TryGetValue(goal, out var result))
                    return false;

                return result.RegisterAGVName != AgvName;
            }
            bool IsCycleStopAllow(IAGV vehicle, MapPoint _entryPointOfWorkStation)
            {
                //判斷未經過進入點
                var pathRemain = vehicle.NavigationState.NextNavigtionPoints.DistinctBy(pt => pt.TagNumber).ToList();

                bool isAlreadyPassEntryPt = pathRemain.Skip(1).All(pt => pt.TagNumber != _entryPointOfWorkStation.TagNumber);

                if (isAlreadyPassEntryPt)
                    return false;

                bool IsDistanceFarwayEntryPoint = _entryPointOfWorkStation.CalculateDistance(vehicle.states.Coordination) >= 3.0;
                int indexOfPtOfCurrent = pathRemain.FindIndex(pt => pt.TagNumber == vehicle.currentMapPoint.TagNumber); //當前點的index 0.1.2.3
                int indexofCycleStopPoint = indexOfPtOfCurrent + 1;
                if (indexofCycleStopPoint == pathRemain.Count)
                    return false;
                var cycleStopPoint = pathRemain[indexofCycleStopPoint];
                double distanceToEntryPointOfCycleStopPt = cycleStopPoint.CalculateDistance(_entryPointOfWorkStation);
                return IsDistanceFarwayEntryPoint && distanceToEntryPointOfCycleStopPt > 2.0;
            }
            bool IsNeedWait(int _goalTag, IAGV agv, IEnumerable<IAGV> _otherAGVList, out bool isTagRegisted, out bool isTagBlocked, out bool isInterference, out bool isInterfercenWhenRotation, out List<IAGV> conflicVehicles)
            {
                isTagRegisted = IsDestineRegisted(_goalTag, agv.Name);
                var goalPoint = StaMap.GetPointByTagNumber(_goalTag);

                bool IsLeaveFromChargeStation = agv.currentMapPoint.IsCharge;
                int indexOfGoalPoint = StaMap.GetIndexOfPoint(goalPoint);
                MapCircleArea _agvCircleAreaWhenReachGoal = agv.AGVRotaionGeometry.Clone();
                _agvCircleAreaWhenReachGoal.SetCenter(goalPoint.X, goalPoint.Y);

                conflicVehicles = new List<IAGV>();

                //var conflicByNavigationPathAGVList = _otherAGVList.Where(_agv => _agv.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal && _agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetCircleArea(ref _agv, 1.2).IsIntersectionTo(goalPoint.GetCircleArea(ref agv, 1.2))))
                //                            .ToList();

                var conflicByNavigationPathAGVList = _otherAGVList.Where(veh => CalculatePathConflicOfVehicle(veh, out var _));
                var conflicByGeometryAGVList = _otherAGVList.Where(agv => agv.AGVRealTimeGeometery.IsIntersectionTo(goalPoint.GetCircleArea(ref agv)));
                var conflicByRotationAGVList = _otherAGVList.Where(agv => _agvCircleAreaWhenReachGoal.IsIntersectionTo(agv.AGVRealTimeGeometery));
                conflicVehicles.AddRange(conflicByNavigationPathAGVList);
                conflicVehicles.AddRange(conflicByGeometryAGVList);
                conflicVehicles.AddRange(conflicByRotationAGVList);
                conflicVehicles = conflicVehicles.DistinctBy(vehi => vehi.Name).ToList();

                bool is_destine_conflic = conflicVehicles.Any();
                bool IsOtherVehicleFeturePathConlic = _otherAGVList.Any(veh => CalculatePathConflicOfVehicle(veh, out var _));

                isInterference = is_destine_conflic || IsOtherVehicleFeturePathConlic || conflicByGeometryAGVList.Any();
                isInterfercenWhenRotation = conflicByRotationAGVList.Any();

                isTagBlocked = IsDestineBlocked();
                if (IsLeaveFromChargeStation)
                {
                    IEnumerable<MapPoint> nearChargeStationEntryPoints = StaMap.Map.Points.Values.Where(pt => pt != agv.currentMapPoint && pt.IsCharge)
                                                                                    .Where(pt => StaMap.GetPointByIndex(pt.Target.Keys.First()).Target.Keys.Contains(indexOfGoalPoint))
                                                                                    .SelectMany(pt => pt.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
                    bool anyNearChargeEnteryPtRegisted = nearChargeStationEntryPoints.Any(pt => StaMap.RegistDictionary.ContainsKey(pt.TagNumber));
                    return isTagRegisted || anyNearChargeEnteryPtRegisted || isTagBlocked;

                }
                else
                    return isTagRegisted || isInterference || isTagBlocked || isInterfercenWhenRotation;


                bool CalculatePathConflicOfVehicle(IAGV vehicle, out List<MapRectangle> conflics)
                {
                    conflics = vehicle.NavigationState.NextPathOccupyRegions.Where(rect => rect.IsIntersectionTo(goalPoint.GetCircleArea(ref vehicle, 1.1))).ToList();
                    return conflics.Any();
                }

            }
            #endregion
        }
        private static void ClsSubTask_OnPathClosedByAGVImpactDetecting(object? sender, List<MapPoint> path_points)
        {
            for (int i = 0; i < path_points.Count; i++)
            {
                if (i + 1 == path_points.Count)
                    break;
                var start_inedx = StaMap.GetIndexOfPoint(path_points[i]);
                var end_index = StaMap.GetIndexOfPoint(path_points[i + 1]);
                string path_id = $"{start_inedx}_{end_index}";
                var path = StaMap.Map.Segments.FirstOrDefault(path => path.PathID == path_id);
                if (path != null)
                {
                    DynamicTrafficState.ControledPathesByTraffic.TryAdd(path_id, path);
                }
                path_id = $"{end_index}_{start_inedx}";
                path = StaMap.Map.Segments.FirstOrDefault(path => path.PathID == path_id);
                if (path != null)
                {
                    DynamicTrafficState.ControledPathesByTraffic.TryAdd(path_id, path);
                }
            }
        }


        public static List<TrafficControlCommander> TrafficEventCommanders = new List<TrafficControlCommander>();

        private static void ClsWaitingInfo_OnAGVWaitingStatusChanged(clsWaitingInfo waitingInfo)
        {
            try
            {
                if (waitingInfo.Status == clsWaitingInfo.WAIT_STATUS.WAITING)
                {
                    AGVWaitingQueue.Add(waitingInfo);
                    LOG.INFO($"AGV-{waitingInfo.Agv.Name} waiting {waitingInfo.WaitingPoint.Name} passable.");
                    //等待點由誰所註冊   

                    bool isRegisted = StaMap.GetPointRegisterName(waitingInfo.WaitingPoint.TagNumber, out string waitingForAGVName);
                    bool isNearPointRegisted = StaMap.GetNearPointRegisterName(waitingInfo.WaitingPoint.TagNumber, waitingInfo.Agv.Name, out string waitingNearAGVName, out int NearAGVPointTag);
                    if (isRegisted || isNearPointRegisted)
                    {
                        if (!isRegisted)
                        {
                            waitingInfo.WaitingPoint = StaMap.GetPointByTagNumber(NearAGVPointTag);
                            waitingForAGVName = waitingNearAGVName;
                            LOG.INFO($"[waitingNearPoint] {waitingInfo.WaitingPoint.TagNumber} ,{waitingNearAGVName}");
                        }
                        LOG.INFO($"[waitingPoint] {waitingInfo.WaitingPoint.TagNumber} ,{waitingNearAGVName}");

                        IAGV agv_ = VMSManager.GetAGVByName(waitingForAGVName);
                        var waingInfoOfAgvRegistPt = AGVWaitingQueue.FirstOrDefault(wait_info => wait_info.Agv == agv_ & wait_info.WaitingPoint == waitingInfo.Agv.currentMapPoint);
                        if (waingInfoOfAgvRegistPt != null)
                        {
                            //互相等待
                            LOG.WARN($"Traffic Lock:{waitingInfo.Agv.Name}({waitingInfo.Agv.currentMapPoint.TagNumber}) and {agv_.Name}({agv_.currentMapPoint.TagNumber})  are waiting for each other");
                            clsTrafficInterLockSolver interlockSolver = new clsTrafficInterLockSolver(waitingInfo.Agv, agv_);
                            InterLockTrafficSituations.Enqueue(interlockSolver);
                        }
                        else
                        {
                            //等待註冊點解除註冊 
                            waitingInfo.AllowMoveResumeResetEvent.WaitOne();
                        }
                    }
                    else
                    {
                        waitingInfo.AllowMoveResumeResetEvent.Set();
                    }
                    //var agv_conflic = VMSManager.GetAGVListExpectSpeficAGV(waitingInfo.Agv.Name).FirstOrDefault(agv => agv.taskDispatchModule.TaskStatusTracker.waitingInfo.WaitingPoint.TagNumber == agv.currentMapPoint.TagNumber)
                }
                else if (waitingInfo.Status == clsWaitingInfo.WAIT_STATUS.NO_WAIT)
                {
                    waitingInfo.AllowMoveResumeResetEvent.Set();
                    LOG.INFO($"AGV-{waitingInfo.Agv.Name} not waiting");
                    if (AGVWaitingQueue.Contains(waitingInfo))
                        AGVWaitingQueue.Remove(waitingInfo);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }

        }


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
                            TaskRecieveTime = agv.main_state != MAIN_STATUS.RUN ? DateTime.MaxValue : agv.taskDispatchModule.TaskStatusTracker.TaskOrder == null ? DateTime.MaxValue : agv.taskDispatchModule.TaskStatusTracker.TaskOrder.RecieveTime,
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

        private static void TrafficInterLockSolveWorker()
        {
            while (true)
            {
                Thread.Sleep(1);
                try
                {
                    if (InterLockTrafficSituations.Count == 0) //No traffic problem to solve
                        continue;

                    if (InterLockTrafficSituations.TryDequeue(out var trafficProblemToSolve))
                    {
                        trafficProblemToSolve.StartSolve();
                    }

                }
                catch (Exception ex)
                {
                    LOG.Critical(ex);
                }
            }
        }

    }

    /// <summary>
    /// 解決交通互鎖的專家^_^
    /// </summary>
    public class clsTrafficInterLockSolver : clsTaskDatabaseWriteableAbstract
    {
        public clsTrafficInterLockSolver(IAGV Agv1, IAGV Agv2)
        {
            this.Agv1 = Agv1;
            this.Agv2 = Agv2;
            this.OccurTime = DateTime.Now;
        }
        public IAGV Agv1 { get; }
        public IAGV Agv2 { get; }


        private IAGV _Agv_Waiting;
        private IAGV _Agv_GoAway;

        public DateTime OccurTime { get; }
        public DateTime StartSolveTime { get; private set; }

        clsTaskDto tafTasOrder;
        internal async void StartSolve()
        {
            StartSolveTime = DateTime.Now;
            _Agv_GoAway = DetermineWhoIsNeedToGetOutAway(out _Agv_Waiting);
            Solve(_Agv_GoAway, _Agv_Waiting);
        }
        private async void Solve(IAGV AgvToGo, IAGV AgvWait)
        {

            bool confirmed = await RaiseAGVGoAwayRequest();
            if (confirmed)
            {
                AgvToGo.IsSolvingTrafficInterLock = true;
                AgvToGo.taskDispatchModule.TaskStatusTracker.waitingInfo.SetStatusNoWaiting(AgvToGo);
                AgvToGo.taskDispatchModule.LastNormalTaskPauseByAvoid = AgvToGo.taskDispatchModule.TaskStatusTracker;
                string canceledTaskName = await AgvToGo.taskDispatchModule.CancelTask(false);
                await Task.Delay(500);

                bool TAFTaskStartOrFinish = await AwaitTAFTaskStart();
                if (TAFTaskStartOrFinish)
                {
                    LOG.INFO($"{AgvToGo.Name} Start Traffic Task-{tafTasOrder.TaskName}");
                    var tagsListPlan = AgvToGo.taskDispatchModule.TaskStatusTracker.SubTaskTracking.EntirePathPlan.Select(p => p.TagNumber).ToList();

                    var tagsNearPoint = StaMap.GetNearPointListByPathAndDistance(tagsListPlan, AgvToGo.options.VehicleLength / 100);
                    await Task.Delay(1000);
                    tagsListPlan.Insert(0, AgvWait.currentMapPoint.TagNumber);
                    LOG.INFO($"Wait {AgvWait.Name} leave Path {string.Join("->", tagsListPlan)} and NearPoint {string.Join(",", tagsNearPoint)}");
                    tagsListPlan.AddRange(tagsNearPoint);
                    bool tafTaskFinish = await AwaitTAFTaskFinish();
                    if (tafTaskFinish)
                    {
                        AgvWait.taskDispatchModule.TaskStatusTracker.waitingInfo.AllowMoveResumeResetEvent.Set();
                    }
                    while (tagsListPlan.Any(TagNumber => TagNumber == AgvWait.currentMapPoint.TagNumber))
                    {
                        await Task.Delay(1);
                    }
                    SetCanceledTaskWait(canceledTaskName);
                    AgvToGo.IsSolvingTrafficInterLock = false;
                    LOG.INFO($"{AgvToGo.Name} cancel Task-{canceledTaskName},and AGV will release {AgvToGo.currentMapPoint.TagNumber}");
                }
                else
                {
                    LOG.ERROR($"?????????");
                }

            }
            else
            {
                //嘗試倒轉
                _Agv_GoAway = Agv1.Name == _Agv_Waiting.Name ? Agv1 : Agv2;
                _Agv_Waiting = Agv1.Name == _Agv_GoAway.Name ? Agv2 : Agv1;
                Solve(_Agv_GoAway, _Agv_Waiting);
            }

        }

        private async Task<bool> AwaitTAFTaskStart()
        {
            while (true)
            {
                Thread.Sleep(1);
                using var agvDB = new AGVSDatabase();
                var task = agvDB.tables.Tasks.FirstOrDefault(tk => tk.TaskName == tafTasOrder.TaskName);
                if (task == null)
                    continue;
                if (task.State == TASK_RUN_STATUS.NAVIGATING | task.State == TASK_RUN_STATUS.ACTION_FINISH && _Agv_GoAway.taskDispatchModule.TaskStatusTracker.SubTaskTracking != null)
                {
                    return true;
                }
                else if (task.State == TASK_RUN_STATUS.FAILURE | task.State == TASK_RUN_STATUS.CANCEL)
                {
                    return false;
                }
                else
                {
                    continue;
                }
            }
        }

        private async Task<bool> AwaitTAFTaskFinish()
        {
            while (true)
            {
                Thread.Sleep(10);
                using var agvDB = new AGVSDatabase();
                var task = agvDB.tables.Tasks.FirstOrDefault(tk => tk.TaskName == tafTasOrder.TaskName);
                if (task == null)
                    continue;
                if (task.State == TASK_RUN_STATUS.ACTION_FINISH)
                    return true;
                else if (task.State == TASK_RUN_STATUS.CANCEL | task.State == TASK_RUN_STATUS.FAILURE)
                    return false;
                else
                    continue;
            }
        }
        private void SetCanceledTaskWait(string canceledTaskName)
        {
            try
            {
                using (var db = new AGVSDatabase())
                {
                    var canceledTask = db.tables.Tasks.FirstOrDefault(t => t.TaskName == canceledTaskName);
                    canceledTask.State = TASK_RUN_STATUS.WAIT;
                    RaiseTaskDtoChange(this, canceledTask);
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }
        /// <summary>
        /// 決定哪一台AGV必須被趕走
        /// </summary>
        /// <returns></returns>
        private IAGV DetermineWhoIsNeedToGetOutAway(out IAGV AgvWaiting)
        {
            AgvWaiting = null;
            var agv1_start_wait_time = Agv1.taskDispatchModule.TaskStatusTracker.waitingInfo.StartWaitingTime;
            var agv2_start_wait_time = Agv1.taskDispatchModule.TaskStatusTracker.waitingInfo.StartWaitingTime;
            if (agv1_start_wait_time < agv2_start_wait_time)
            {
                AgvWaiting = Agv2;
                return Agv1;
            }
            else
            {
                AgvWaiting = Agv1;
                return Agv2;
            }
        }
        internal async Task<bool> RaiseAGVGoAwayRequest()
        {
            var pathes_of_waiting_agv = _Agv_Waiting.taskDispatchModule.TaskStatusTracker.SubTaskTracking.EntirePathPlan;
            var WaitingAGVCurrentIndex = pathes_of_waiting_agv.IndexOf(_Agv_Waiting.currentMapPoint);
            var FollowingPathesOfWaitingAGV = new MapPoint[pathes_of_waiting_agv.Count - WaitingAGVCurrentIndex];
            pathes_of_waiting_agv.CopyTo(WaitingAGVCurrentIndex, FollowingPathesOfWaitingAGV, 0, FollowingPathesOfWaitingAGV.Length);
            var PathesNearPointOfWaitingAGV = StaMap.GetNearPointListByPathAndDistance(FollowingPathesOfWaitingAGV.Select(item => item.TagNumber).ToList(), _Agv_Waiting.options.VehicleLength / 100);
            var pathTags = FollowingPathesOfWaitingAGV.Select(pt => pt.TagNumber).ToList();
            MapPoint destinePt = pathes_of_waiting_agv.Last();
            LOG.WARN($"{_Agv_Waiting.Name} raise Fucking Stupid AGV Go Away(AGV Should Leave Tag{string.Join(",", pathTags)}) Rquest");
            TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();
            List<int> tagListAvoid = new List<int>();
            tagListAvoid.AddRange(VMSManager.AllAGV.Where(_agv => _agv.Name != _Agv_Waiting.Name).Select(_agv => _agv.states.Last_Visited_Node));
            tagListAvoid.AddRange(pathTags);
            tagListAvoid.AddRange(PathesNearPointOfWaitingAGV);

            var ptToParking = FindTagToParking(_Agv_GoAway.states.Last_Visited_Node, tagListAvoid, _Agv_GoAway.options.VehicleLength / 100.0);
            if (ptToParking == -1)
            {
                LOG.TRACE($"[Traffic] {_Agv_Waiting.Name} Request {_Agv_GoAway.Name} Far away from {_Agv_GoAway.currentMapPoint.TagNumber} but no tag can park.");
                return false;
            }
            LOG.TRACE($"[Traffic] Request {_Agv_GoAway.Name} Go to Tag {ptToParking}");
            tafTasOrder = new clsTaskDto
            {
                Action = ACTION_TYPE.None,
                TaskName = $"TAF_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                DesignatedAGVName = _Agv_GoAway.Name,
                Carrier_ID = "",
                DispatcherName = "Traffic",
                RecieveTime = DateTime.Now,
                Priority = 99,
                To_Station = ptToParking.ToString(),
                IsTrafficControlTask = true
            };
            await TaskDBHelper.Add(tafTasOrder);
            return true;
        }
        private int FindTagToParking(int startTag, List<int> avoidTagList, double vechiel_length)
        {
            List<int> _avoidTagList = new List<int>();
            _avoidTagList.Add(startTag);
            _avoidTagList.AddRange(avoidTagList);
            var startPT = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == startTag);
            var AvoidTagNearPointsDistance = StaMap.Dict_AllPointDistance.Where(ptData => _avoidTagList.Contains(ptData.Key));
            List<int> List_NearPoint = new List<int>();
            foreach (var item in AvoidTagNearPointsDistance)
            {
                foreach (var TagDistance in item.Value)
                {
                    if (TagDistance.Value > _Agv_GoAway.options.VehicleLength / 100)
                        continue;
                    List_NearPoint.Add(TagDistance.Key);
                }
            }
            List_NearPoint = List_NearPoint.Distinct().ToList();

            var ptAvaliable = StaMap.Map.Points.Values.Where(pt => pt.StationType == STATION_TYPE.Normal && !pt.IsVirtualPoint && pt.Enable).ToList().FindAll(pt => !_avoidTagList.Contains(pt.TagNumber) && !List_NearPoint.Contains(pt.TagNumber));

            //計算出所有路徑
            PathFinder pf = new PathFinder();
            List<clsPathInfo> pfResultCollection = new List<clsPathInfo>();
            foreach (var validPT in ptAvaliable)
            {
                var pfResult = pf.FindShortestPath(StaMap.Map, startPT, validPT, new PathFinderOption { OnlyNormalPoint = true });
                if (pfResult != null)
                    pfResultCollection.Add(pfResult);
            }
            //依據走行總距離由小至大排序
            pfResultCollection = pfResultCollection.Where(pf => pf.waitPoints.Count == 0).OrderBy(pf => pf.total_travel_distance).ToList();
            if (pfResultCollection == null)
                return -1;
            var otherAgvTags = VMSManager.AllAGV.FilterOutAGVFromCollection(_Agv_GoAway).Select(agv => agv.currentMapPoint.TagNumber).ToList();
            otherAgvTags.AddRange(StaMap.Dict_AllPointDistance[_Agv_Waiting.currentMapPoint.TagNumber].Where(item => item.Value < _Agv_Waiting.options.VehicleLength / 100).Select(item => item.Key).ToList());
            if (pfResultCollection.Count > 0)
            {
                //過濾出不會與[要求趕車之AGV]路徑衝突的路徑
                pfResultCollection = pfResultCollection.Where(pf => pf.tags.Any(tag => !_avoidTagList.Contains(tag))).ToList();
                var _avoidMapPoints = StaMap.Map.Points.Values.ToList().FindAll(pt => _avoidTagList.Contains(pt.TagNumber));

                pfResultCollection = pfResultCollection.Where(pf => _avoidMapPoints.All(pt => pt.CalculateDistance(pf.stations.Last()) >= vechiel_length)).ToList();
                pfResultCollection = pfResultCollection.Where(pf => pf.tags.All(tag => !otherAgvTags.Contains(tag))).ToList();
                if (pfResultCollection.Count == 0)
                    return -1;
                return pfResultCollection.First().stations.Last().TagNumber;
            }
            else
                return -1;
        }
    }
}

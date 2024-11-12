using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.YieldActions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using WebSocketSharp;

namespace VMSystem.Dispatch
{
    public partial class DeadLockMonitor
    {

        public static Dictionary<ACTION_TYPE, int> OrderActionWeightsMap = new Dictionary<ACTION_TYPE, int>
        {
            { ACTION_TYPE.Carry , 1000 },
            { ACTION_TYPE.Unload , 900 },
            { ACTION_TYPE.Load, 800 },
            { ACTION_TYPE.None, 500 },
            { ACTION_TYPE.Charge, 400 },
        };

        public List<Tuple<IAGV, MapPath>> TempRemovedMapPathes = new List<Tuple<IAGV, MapPath>>();

        private IEnumerable<IAGV> WaitingConflicReleaseVehicles
        {
            get
            {
                return VMSManager.AllAGV.Where(v => IsWaitingConflicRegionRelease(v));
            }
        }

        private IEnumerable<IAGV> WaitingLeaveWorkStationVehicles
        {
            get
            {
                return VMSManager.AllAGV.Where(vehicle => vehicle.NavigationState.IsWaitingForLeaveWorkStationTimeout);
            }
        }

        public async Task StartAsync()
        {
            Task.Run(() => StartNavigationConflicMonitor());
            Task.Run(() => StartLeaveWorkStationDeadLockMonitor());
        }

        private async void StartNavigationConflicMonitor()
        {
            await Task.Delay(1);

            while (true)
            {
                await Task.Delay(100);
                try
                {
                    if (WaitingConflicReleaseVehicles.Count() > 1)
                    {
                        var _deadLockVehicles = WaitingConflicReleaseVehicles.ToArray();

                        (IAGV firstWaitingVehicle, IAGV second) GetDeadLockVehiclePair(IAGV vehicleSearch)
                        {

                            IAGV firstWaitingVehicle = vehicleSearch;
                            IAGV second = _deadLockVehicles.Where(agv => agv.Name != firstWaitingVehicle.Name)
                                                           .Where(agv => agv.NavigationState.currentConflicToAGV?.Name == firstWaitingVehicle?.Name)
                                                           .FirstOrDefault();
                            return (firstWaitingVehicle, second);
                        }

                        foreach (var vehicle in _deadLockVehicles)
                        {
                            (IAGV _firstWaitingVehicle, IAGV _secondVehicle) = GetDeadLockVehiclePair(vehicle);
                            if (_secondVehicle != null)
                            {
                                _firstWaitingVehicle.NavigationState.IsConflicSolving = _secondVehicle.NavigationState.IsConflicSolving = true;
                                await DeadLockSolve(new List<IAGV>() { _firstWaitingVehicle, _secondVehicle });
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    foreach (var item in WaitingConflicReleaseVehicles)
                    {
                        item.NavigationState.IsConflicSolving = false;
                    }
                }

            }
        }

        private async void StartLeaveWorkStationDeadLockMonitor()
        {
            await Task.Delay(1);

            while (true)
            {
                await Task.Delay(100);
                try
                {
                    if (WaitingLeaveWorkStationVehicles.Count() > 1)
                    {
                        (IAGV lowPriorityVehicle, IAGV highPriorityVehicle, bool _) = DeterminPriorityOfVehicles(WaitingLeaveWorkStationVehicles.ToList());
                        lowPriorityVehicle.NavigationState.IsWaitingForLeaveWorkStationTimeout =
                            highPriorityVehicle.NavigationState.IsWaitingForLeaveWorkStationTimeout = false;

                        clsLowPriorityVehicleWaitAtWorkStation _solver = new clsLowPriorityVehicleWaitAtWorkStation(lowPriorityVehicle, highPriorityVehicle);
                        await _solver.StartSolve();
                    }
                }
                catch (Exception ex)
                {
                    foreach (var item in WaitingConflicReleaseVehicles)
                    {
                        item.NavigationState.IsConflicSolving = false;
                    }
                }

            }
        }

        private bool IsWaitingConflicRegionRelease(IAGV vehicle)
        {
            return vehicle.NavigationState.IsWaitingConflicSolve;
            //return vehicle.NavigationState.IsWaitingConflicSolve && !vehicle.NavigationState.IsConflicSolving;
        }

        private async Task<IAGV> DeadLockSolve(IEnumerable<IAGV> DeadLockVehicles)
        {
            //決定誰要先移動到避車點

            //Dictionary<IAGV, MapPoint> parkStationState = DeadLockVehicles.ToDictionary(vehicle => vehicle, vehicle => GetParkableStationOfCurrentRegion(vehicle));

            //if (parkStationState.Any(pair => pair.Value != null))
            //{
            //    var _lpPair = parkStationState.First(pair => pair.Value != null);
            //    var _lowProrityVehicle = _lpPair.Key;
            //    var avoidPoint = _lpPair.Value;

            //    var _highProrityVehicle = parkStationState.First(pair => pair.Key.Name != _lowProrityVehicle.Name).Key;
            //    _lowProrityVehicle.NavigationState.AvoidActionState.AvoidAction = ACTION_TYPE.Park;
            //    _lowProrityVehicle.NavigationState.AvoidActionState.AvoidToVehicle = _highProrityVehicle;
            //    _lowProrityVehicle.NavigationState.AvoidActionState.AvoidPt = avoidPoint;
            //    _lowProrityVehicle.NavigationState.AvoidActionState.AvoidToVehicle = _highProrityVehicle;
            //    _lowProrityVehicle.NavigationState.AvoidActionState.IsAvoidRaising = true;
            //    return _lowProrityVehicle;
            //}

            (IAGV lowPriorityVehicle, IAGV highPriorityVehicle, bool IsAvoidUseParkablePort) = DeterminPriorityOfVehicles(DeadLockVehicles);

            if (IsAvoidUseParkablePort)
            {
                clsAvoidWithParkablePort avoidAction = new clsAvoidWithParkablePort(lowPriorityVehicle, highPriorityVehicle);
                var _avoidVehicle = await avoidAction.StartSolve();
                if (_avoidVehicle != null)
                {
                    lowPriorityVehicle.NavigationState.AvoidActionState.IsParkToWIPButNoPathToGo = false;
                    return _avoidVehicle;
                }
                else
                    lowPriorityVehicle.NavigationState.AvoidActionState.IsParkToWIPButNoPathToGo = true;

            }
            clsLowPriorityVehicleMove lowPriorityWork = new clsLowPriorityVehicleMove(lowPriorityVehicle, highPriorityVehicle);
            var toAvoidVehicle = await lowPriorityWork.StartSolve();
            if (toAvoidVehicle == null)
            {
                lowPriorityWork = new clsLowPriorityVehicleMove(highPriorityVehicle, lowPriorityVehicle);
                toAvoidVehicle = await lowPriorityWork.StartSolve();
            }
            await Task.Delay(200);
            return toAvoidVehicle;
        }

        public static (IAGV lowPriorityVehicle, IAGV highPriorityVehicle, bool IsAvoidUseParkablePort) DeterminPriorityOfVehicles(IEnumerable<IAGV> DeadLockVehicles)
        {

            //如果是叉車與潛盾互等 而且叉車所在區域內有可停車的WIP=> 叉車停進去WIP避讓。
            if (TrafficControlCenter.TrafficControlParameters.Experimental.UseRackToAvoid && DeadLockVehicles.Any(vehicle => vehicle.model == clsEnums.AGV_TYPE.FORK && !vehicle.NavigationState.AvoidActionState.IsParkToWIPButNoPathToGo))
            {
                IAGV forkAGV = DeadLockVehicles.FirstOrDefault(vehicle => vehicle.model == clsEnums.AGV_TYPE.FORK);
                if (forkAGV != null && GetParkablePointOfAGVInRegion(forkAGV).Any())
                {
                    IAGV HighPriortyAGV = DeadLockVehicles.First(v => v != forkAGV);
                    NotifyServiceHelper.INFO($"叉車-{forkAGV.Name}應優先避讓至WIP PORT");
                    return (forkAGV, HighPriortyAGV, true);
                }
            }

            //if (_IsAnyAGVInfrontOfChargeStation(out IAGV _agv))
            //{
            //    var _lowPAGV = DeadLockVehicles.First(agv => _IsAnyAGVInNearbyOfDestineOfOthers(agv));
            //    var _highPAGV = DeadLockVehicles.First(agv => agv != _lowPAGV);
            //    NotifyServiceHelper.INFO($"{_lowPAGV.Name} 位於充電站前.應優先進入充電站避讓");
            //    return (_lowPAGV, _highPAGV, true);
            //}

            //如果有某台車他位於另一台車之終點的相鄰位置=>先避讓
            if (DeadLockVehicles.FirstOrDefault(agv => _IsAnyAGVInNearbyOfDestineOfOthers(agv)) != null)
            {
                var _lowPAGV = DeadLockVehicles.First(agv => _IsAnyAGVInNearbyOfDestineOfOthers(agv));
                var _highPAGV = DeadLockVehicles.First(agv => agv != _lowPAGV);
                NotifyServiceHelper.INFO($"{_lowPAGV.Name} 位於其他車輛任務終點或鄰近點.應優先避讓");
                return (_lowPAGV, _highPAGV, false);
            }

            Dictionary<IAGV, int> orderedByWeight = DeadLockVehicles.ToDictionary(v => v, v => CalculateWeights(v));
            IEnumerable<IAGV> ordered = new List<IAGV>();
            if (orderedByWeight.First().Value == orderedByWeight.Last().Value)
            {
                //權重相同,先等待者為高優先權車輛
                ordered = DeadLockVehicles.OrderBy(vehicle => (DateTime.Now - vehicle.NavigationState.StartWaitConflicSolveTime).TotalSeconds);
            }
            else
                ordered = orderedByWeight.OrderBy(kp => kp.Value).Select(kp => kp.Key);
            return (ordered.First(), ordered.Last(), false);

            IEnumerable<MapPoint> GetParkablePointOfAGVInRegion(IAGV forkAGV)
            {
                IEnumerable<MapPoint> parkablePortPointsInRegion = forkAGV.currentMapPoint.GetRegion().GetParkablePointOfRegion(forkAGV);
                if (!parkablePortPointsInRegion.Any())
                    return new List<MapPoint>();

                //

                return parkablePortPointsInRegion;
            }


            //判斷AGV是否在其他車輛任務的終點或鄰近點
            bool _IsAnyAGVInNearbyOfDestineOfOthers(IAGV _agv)
            {
                var otherAgvList = DeadLockVehicles.Where(agv => agv != _agv);
                var otherAGVDestineMapPoints = otherAgvList.Select(agv => StaMap.GetPointByTagNumber(agv.CurrentRunningTask().DestineTag)).ToList();
                bool _isAGVAtSomeoneDestine = otherAGVDestineMapPoints.Any(pt => pt.TagNumber == _agv.currentMapPoint.TagNumber);
                if (_isAGVAtSomeoneDestine)
                    return true;
                var allNearbyPtOfDestines = otherAGVDestineMapPoints.SelectMany(destinePt => destinePt.TargetNormalPoints());
                bool _isAGVAtSomeoneNearPointOfDestine = allNearbyPtOfDestines.Any(pt => pt.TagNumber == _agv.currentMapPoint.TagNumber);
                return _isAGVAtSomeoneNearPointOfDestine;
            }

            bool _IsAnyAGVInfrontOfChargeStation(out IAGV _agv)
            {
                _agv = DeadLockVehicles.FirstOrDefault(agv => agv.currentMapPoint.TargetWorkSTationsPoints().Any(pt => pt.IsCharge));
                return _agv != null;
            }

        }
        public static int CalculateWeights(IAGV vehicle)
        {
            var currentOrderHandler = vehicle.CurrentOrderHandler();
            var runningTask = currentOrderHandler.RunningTask;
            var runningStage = runningTask.Stage;
            var orderInfo = currentOrderHandler.OrderData;
            var orderAction = orderInfo.Action;

            int weights = OrderActionWeightsMap[orderAction];

            if (orderAction == ACTION_TYPE.Carry)
            {
                if (runningStage == VehicleMovementStage.Traveling_To_Source)
                    weights += 50;
                else
                    weights += 40;
            }

            if (orderAction == ACTION_TYPE.Carry || orderAction == ACTION_TYPE.Load || orderAction == ACTION_TYPE.Unload)
            {
                if (orderAction != ACTION_TYPE.Carry)
                {
                    int workStationTag = 0;
                    workStationTag = orderInfo.To_Station_Tag;
                    MapPoint workStationPt = StaMap.GetPointByTagNumber(workStationTag);
                    weights = weights * workStationPt.PriorityOfTask;
                }
                else
                {
                    MapPoint sourcePt = StaMap.GetPointByTagNumber(orderInfo.From_Station_Tag);
                    MapPoint destinePt = StaMap.GetPointByTagNumber(orderInfo.To_Station_Tag);
                    weights = weights * sourcePt.PriorityOfTask * destinePt.PriorityOfTask;

                }
            }

            return weights;
        }

        internal async void HandleVehicleNoWaitConflicSolve(object? sender, IAGV waitingVehicle)
        {
            var stored = TempRemovedMapPathes.FirstOrDefault(store => store.Item1 == waitingVehicle);
            if (stored == null)
                return;

            MapPath pathToResotre = stored.Item2;
            if (StaMap.AddPathDynamic(pathToResotre))
            {
                TempRemovedMapPathes.Remove(stored);
                NotifyServiceHelper.SUCCESS($"{waitingVehicle.Name} 與在設備中的車輛解除互相等待! 重新開啟路徑(ID= {pathToResotre.PathID})");
            }
        }

        internal static MapPoint GetParkableStationOfCurrentRegion(IAGV agvToPark)
        {

            bool _isAGVHasCargo = agvToPark.states.Cargo_Status == 1 || agvToPark.states.CSTID.Any(_id => !_id.IsNullOrEmpty());
            if (_isAGVHasCargo)
                return null;

            //找到所有可停車
            var currentRegion = agvToPark.currentMapPoint.GetRegion();
            //在該區域所有的主幹道點位
            var normalPointsInThisRegion = StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && pt.GetRegion().Name == currentRegion.Name);

            //過濾掉點位對應的設備有其他車輛要過來進行任務
            normalPointsInThisRegion = normalPointsInThisRegion.Where(pt => !_IsEQOfNormalPointHasTask(pt));

            //找到所有可停車的點位(Key = Entry Point , Value= Stations)
            var parkables = normalPointsInThisRegion.ToDictionary(pt => pt, pt => pt.TargetParkableStationPoints(ref agvToPark));
            if (parkables.Any(pair => pair.Value.Any()))
            {
                parkables = parkables.Where(pair => pair.Key != null && pair.Value.Any())
                                     .OrderBy(obj => obj.Key.CalculateDistance(agvToPark.states.Coordination))
                                     .ToDictionary(obj => obj.Key, obj => obj.Value);
                return parkables.First().Value.First();
            }
            else
            {
                return null;
            }

            bool _IsEQOfNormalPointHasTask(MapPoint pt)
            {
                IEnumerable<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(agvToPark);
                IEnumerable<int> otherVehiclesCurrentGoalTags = otherVehicles.Select(vehicle => vehicle.CurrentRunningTask().DestineTag);
                return pt.TargetWorkSTationsPoints().Any(pt => otherVehiclesCurrentGoalTags.Contains(pt.TagNumber));
            }
        }

        internal async void HandleVehicleStartWaitConflicSolve(object? sender, IAGV waitingVehicle)
        {
            var otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(waitingVehicle);

            if (_IsWaitForVehicleAtWorkStationNear(out IEnumerable<IAGV> vehiclesAtWorkStation))
            {
                await DynamicPathClose(waitingVehicle, vehiclesAtWorkStation);
            }
            if (_IsWaitForVehicleAtWaitingPointOfAnyRegion(out IAGV vehicleWaitingEntry, out MapRegion _region))
            {
                //MapPoint parkStation = GetParkableStationOfCurrentRegion(waitingVehicle);
                //if (parkStation != null)
                //{
                //    waitingVehicle.NavigationState.AvoidActionState.AvoidPt = parkStation;
                //    waitingVehicle.NavigationState.AvoidActionState.AvoidToVehicle = vehicleWaitingEntry;
                //    waitingVehicle.NavigationState.AvoidActionState.AvoidAction = ACTION_TYPE.Park;
                //    waitingVehicle.NavigationState.AvoidActionState.IsAvoidRaising = true;
                //}
                //else
                //{
                //    clsLowPriorityVehicleMove lowPriorityWork = new clsLowPriorityVehicleMove(waitingVehicle, vehicleWaitingEntry);
                //    var toAvoidVehicle = await lowPriorityWork.StartSolve();
                //}
            }
            //是否與停在設備中的車輛互相停等
            bool _IsWaitForVehicleAtWorkStationNear(out IEnumerable<IAGV> vehiclesAtWorkStation)
            {
                vehiclesAtWorkStation = otherVehicles.Where(v => v.currentMapPoint.StationType != MapPoint.STATION_TYPE.Normal);
                //vehiclesAtWorkStation = otherVehicles.Where(v => v.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                //                                     .Where(v => v.CurrentRunningTask().ActionType != ACTION_TYPE.None)
                //                                     .Where(v => v.NavigationState.IsWaitingForLeaveWorkStation);
                return vehiclesAtWorkStation.Any();
            }

            // 是否與其他車輛互相等待進入區域
            bool _IsWaitForVehicleAtWaitingPointOfAnyRegion(out IAGV vehicleWaitingEntry, out MapRegion region)
            {
                MapRegion currentRegion = waitingVehicle.currentMapPoint.GetRegion();
                region = currentRegion;
                var waitingForEntryRegionVehicles = otherVehicles.Where(agv => agv.NavigationState.RegionControlState.NextToGoRegion.Name == currentRegion.Name)
                                                                 .Where(agv => agv.NavigationState.currentConflicToAGV?.Name == waitingVehicle.Name && waitingVehicle.NavigationState.currentConflicToAGV?.Name == agv.Name)
                                                                 .Where(agv => (agv.NavigationState.RegionControlState.IsWaitingForEntryRegion || agv.CurrentRunningTask().Stage == VehicleMovementStage.Traveling_To_Region_Wait_Point));
                vehicleWaitingEntry = null;

                if (!waitingForEntryRegionVehicles.Any())
                    return false;

                MapPoint nextGoalPt = StaMap.GetPointByTagNumber(waitingVehicle.CurrentRunningTask().DestineTag);
                IEnumerable<MapPoint> pathToGoal = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(waitingVehicle.currentMapPoint, nextGoalPt, null);
                IEnumerable<MapPoint> outOfRegionPoints = pathToGoal.Skip(1).Where(pt => pt.GetRegion().Name != currentRegion.Name);
                IEnumerable<MapRegion> otherVehicleCurrentRegions = waitingForEntryRegionVehicles.Select(agv => agv.currentMapPoint.GetRegion());

                return outOfRegionPoints.Any(pt => otherVehicleCurrentRegions.Any(reg => reg.Name == pt.GetRegion().Name));

            }

            bool _TryGetParkableStation(IEnumerable<MapPoint> value, out MapPoint? _parkablePoint)
            {
                List<int> _forbiddenTags = waitingVehicle.model == clsEnums.AGV_TYPE.SUBMERGED_SHIELD ? StaMap.Map.TagForbiddenForSubMarineAGV.ToList() : StaMap.Map.TagNoStopOfForkAGV.ToList();
                _parkablePoint = value.FirstOrDefault(pt => !_forbiddenTags.Contains(pt.TagNumber));
                return _parkablePoint != null;
            }
        }


        private async Task DynamicPathClose(IAGV waitingVehicle, IEnumerable<IAGV> vehiclesAtWorkStation)
        {
            MapRectangle CurrentConflicRegion = waitingVehicle.NavigationState.CurrentConflicRegion;
            if (CurrentConflicRegion == null)
                return;
            //start tag / end tag

            var AvoidToVehicle = vehiclesAtWorkStation.First();

            MapPoint eqEntryPoint = AvoidToVehicle.currentMapPoint.TargetNormalPoints().FirstOrDefault();

            if (eqEntryPoint == null)
                return;

            //要把等待中AGV到設備的路徑移除
            int tagOfEntryPointOfEq = eqEntryPoint.TagNumber;
            MapPoint entryPoint = StaMap.Map.Points.Values.First(pt => pt.TagNumber == tagOfEntryPointOfEq);

            if (entryPoint.TagNumber != CurrentConflicRegion.StartPoint.TagNumber && entryPoint.TagNumber != CurrentConflicRegion.EndPoint.TagNumber)
                return;

            double thetaOfEQTrajectory = Math.Abs(Tools.CalculationForwardAngle(entryPoint, AvoidToVehicle.currentMapPoint));
            double thetaOfConflicPath = Math.Abs(Tools.CalculationForwardAngle(CurrentConflicRegion.StartPoint, CurrentConflicRegion.EndPoint));
            double thetaRelative = Math.Abs(thetaOfEQTrajectory - thetaOfConflicPath);
            bool isRotationFrontEqStation = Math.Abs(90 - thetaRelative) > 10;

            if (!isRotationFrontEqStation)
                return;

            var _stored = TempRemovedMapPathes.FirstOrDefault(store => store.Item1 == waitingVehicle);
            if (_stored == null)
            {
                MapPoint startPtOfPathClose = new();
                MapPoint endPtOfPathClose = new();

                bool _isForwardToEQPathConflic = entryPoint.TagNumber == CurrentConflicRegion.EndPoint.TagNumber; //朝向設備的路徑干涉
                if (_isForwardToEQPathConflic)
                {
                    startPtOfPathClose = CurrentConflicRegion.StartPoint;
                    endPtOfPathClose = entryPoint;
                }
                else
                {
                    startPtOfPathClose = entryPoint;
                    endPtOfPathClose = CurrentConflicRegion.EndPoint;
                }

                int indexOfEndPt = StaMap.GetIndexOfPoint(endPtOfPathClose);
                StaMap.Map.Points.Values.First(pt => pt.TagNumber == startPtOfPathClose.TagNumber)
                                         .Target.Remove(indexOfEndPt, out _);

                (bool confirmed, MapPath path) = await StaMap.TryRemovePathDynamic(startPtOfPathClose, endPtOfPathClose);
                if (confirmed)
                {
                    TempRemovedMapPathes.Add(new Tuple<IAGV, MapPath>(waitingVehicle, path));
                    NotifyServiceHelper.WARNING($"{waitingVehicle.Name} 與在設備中的車輛({AvoidToVehicle.Name})互相等待! Close Path-{startPtOfPathClose.TagNumber}->{endPtOfPathClose.TagNumber}");
                }
            }
        }

    }
}

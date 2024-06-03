using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.YieldActions;
using VMSystem.TrafficControl;
using VMSystem.VMS;

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

                        IAGV firstWaitingVehicle = _deadLockVehicles.First();
                        IAGV second = _deadLockVehicles.Where(agv => agv.Name != firstWaitingVehicle.Name)
                                         .Where(agv => agv.NavigationState.currentConflicToAGV.Name == firstWaitingVehicle.Name)
                                         .FirstOrDefault();

                        if (second != null)
                        {
                            firstWaitingVehicle.NavigationState.IsConflicSolving = second.NavigationState.IsConflicSolving = true;
                            await DeadLockSolve(new List<IAGV>() { firstWaitingVehicle, second });
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
                        (IAGV lowPriorityVehicle, IAGV highPriorityVehicle) = DeterminPriorityOfVehicles(WaitingLeaveWorkStationVehicles.ToList());
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

            Dictionary<IAGV, MapPoint> parkStationState = DeadLockVehicles.ToDictionary(vehicle => vehicle, vehicle => GetParkableStation(vehicle));

            if (parkStationState.Any(pair => pair.Value != null))
            {
                var _lpPair = parkStationState.First(pair => pair.Value != null);
                var _lowProrityVehicle = _lpPair.Key;
                var avoidPoint = _lpPair.Value;

                var _highProrityVehicle = parkStationState.First(pair => pair.Key.Name != _lowProrityVehicle.Name).Key;
                _lowProrityVehicle.NavigationState.AvoidActionState.AvoidToVehicle = _highProrityVehicle;

                _lowProrityVehicle.NavigationState.AvoidActionState.AvoidPt = avoidPoint;
                _lowProrityVehicle.NavigationState.AvoidActionState.AvoidToVehicle = _highProrityVehicle;
                _lowProrityVehicle.NavigationState.AvoidActionState.AvoidAction = ACTION_TYPE.Park;
                _lowProrityVehicle.NavigationState.AvoidActionState.IsAvoidRaising = true;
                return _lowProrityVehicle;
            }

            (IAGV lowPriorityVehicle, IAGV highPriorityVehicle) = DeterminPriorityOfVehicles(DeadLockVehicles);
            clsLowPriorityVehicleMove lowPriorityWork = new clsLowPriorityVehicleMove(lowPriorityVehicle, highPriorityVehicle);
            var toAvoidVehicle = await lowPriorityWork.StartSolve();
            await Task.Delay(200);
            return toAvoidVehicle;
        }

        public static (IAGV lowPriorityVehicle, IAGV highPriorityVehicle) DeterminPriorityOfVehicles(IEnumerable<IAGV> DeadLockVehicles)
        {

            Dictionary<IAGV, int> orderedByWeight = DeadLockVehicles.ToDictionary(v => v, v => CalculateWeights(v));
            IEnumerable<IAGV> ordered = new List<IAGV>();
            if (orderedByWeight.First().Value == orderedByWeight.Last().Value)
            {
                //權重相同,先等待者為高優先權車輛
                ordered = DeadLockVehicles.OrderBy(vehicle => (DateTime.Now - vehicle.NavigationState.StartWaitConflicSolveTime).TotalSeconds);
            }
            else
                ordered = orderedByWeight.OrderBy(kp => kp.Value).Select(kp => kp.Key);
            return (ordered.First(), ordered.Last());
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

        internal static MapPoint GetParkableStation(IAGV agvToPark)
        {
            var currentRegion = agvToPark.currentMapPoint.GetRegion(StaMap.Map);
            var normalPointsInThisRegin = StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && pt.GetRegion(StaMap.Map).Name == currentRegion.Name)
                                                                   .OrderBy(pt => pt.CalculateDistance(agvToPark.states.Coordination));
            var normalPointsWithWorkStationEntryAable = normalPointsInThisRegin.ToDictionary(pt => pt, pt => pt.TargetWorkSTationsPoints());
            var registedTags = StaMap.RegistDictionary.Keys.ToList();
            KeyValuePair<MapPoint, IEnumerable<MapPoint>> parkableStationEntrys = normalPointsWithWorkStationEntryAable.Where(pair => pair.Value.Any() && pair.Value.All(pt => !registedTags.Contains(pt.TagNumber)) && pair.Value.All(pt => pt.IsParking))
                                                                                                                        .FirstOrDefault();

            if (parkableStationEntrys.Key != null)
            {
                var _parkableStations = parkableStationEntrys.Value;
                if (agvToPark.model == clsEnums.AGV_TYPE.SUBMERGED_SHIELD)
                {
                    _parkableStations = _parkableStations.Where(pt => pt.StationType != MapPoint.STATION_TYPE.Buffer && pt.StationType != MapPoint.STATION_TYPE.Charge_Buffer);
                }
                _TryGetParkableStation(_parkableStations, out MapPoint _parkStation);
                return _parkStation;
            }
            else
            {
                return null;
            }
            bool _TryGetParkableStation(IEnumerable<MapPoint> value, out MapPoint? _parkablePoint)
            {
                List<int> _forbiddenTags = agvToPark.model == clsEnums.AGV_TYPE.SUBMERGED_SHIELD ? StaMap.Map.TagNoStopOfSubmarineAGV : StaMap.Map.TagNoStopOfForkAGV;
                _parkablePoint = value.FirstOrDefault(pt => !_forbiddenTags.Contains(pt.TagNumber));
                return _parkablePoint != null;
            }
        }

        internal async void HandleVehicleStartWaitConflicSolve(object? sender, IAGV waitingVehicle)
        {
            var otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(waitingVehicle);

            if (_IsWaitForVehicleAtWorkStationNear(out IEnumerable<IAGV> vehiclesAtWorkStation))
            {
                DynamicPathClose(waitingVehicle, vehiclesAtWorkStation);
            }
            if (_IsWaitForVehicleAtWaitingPointOfAnyRegion(out IAGV vehicleWaitingEntry, out MapRegion _region))
            {
                MapPoint parkStation = GetParkableStation(waitingVehicle);

                if (parkStation != null)
                {
                    waitingVehicle.NavigationState.AvoidActionState.AvoidPt = parkStation;
                    waitingVehicle.NavigationState.AvoidActionState.AvoidToVehicle = vehicleWaitingEntry;
                    waitingVehicle.NavigationState.AvoidActionState.AvoidAction = ACTION_TYPE.Park;
                    waitingVehicle.NavigationState.AvoidActionState.IsAvoidRaising = true;
                }
                else
                {
                    clsLowPriorityVehicleMove lowPriorityWork = new clsLowPriorityVehicleMove(waitingVehicle, vehicleWaitingEntry);
                    var toAvoidVehicle = await lowPriorityWork.StartSolve();
                }
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

            bool _IsWaitForVehicleAtWaitingPointOfAnyRegion(out IAGV vehicleWaitingEntry, out MapRegion region)
            {
                MapRegion currentRegion = waitingVehicle.currentMapPoint.GetRegion(StaMap.Map);
                region = currentRegion;
                var waitingForEntryRegionVehicles = otherVehicles.Where(agv => (agv.NavigationState.IsWaitingForEntryRegion || agv.CurrentRunningTask().Stage == VehicleMovementStage.Traveling_To_Region_Wait_Point) && agv.NavigationState.RegionControlState.NextToGoRegion.Name == currentRegion.Name);
                vehicleWaitingEntry = null;
                if (!waitingForEntryRegionVehicles.Any())
                    return false;
                //Agv.NavigationState.RegionControlState.NextToGoRegion 
                vehicleWaitingEntry = waitingForEntryRegionVehicles.FirstOrDefault();
                return vehicleWaitingEntry != null;
            }

            bool _TryGetParkableStation(IEnumerable<MapPoint> value, out MapPoint? _parkablePoint)
            {
                List<int> _forbiddenTags = waitingVehicle.model == clsEnums.AGV_TYPE.SUBMERGED_SHIELD ? StaMap.Map.TagNoStopOfSubmarineAGV : StaMap.Map.TagNoStopOfForkAGV;
                _parkablePoint = value.FirstOrDefault(pt => !_forbiddenTags.Contains(pt.TagNumber));
                return _parkablePoint != null;
            }
        }


        private void DynamicPathClose(IAGV waitingVehicle, IEnumerable<IAGV> vehiclesAtWorkStation)
        {
            MapRectangle CurrentConflicRegion = waitingVehicle.NavigationState.CurrentConflicRegion;
            if (CurrentConflicRegion == null)
                return;
            //start tag / end tag

            var AvoidToVehicle = vehiclesAtWorkStation.First();
            //要把等待中AGV到設備的路徑移除
            int tagOfEntryPointOfEq = AvoidToVehicle.currentMapPoint.TargetNormalPoints().First().TagNumber;
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

                if (StaMap.TryRemovePathDynamic(startPtOfPathClose, endPtOfPathClose, out MapPath path))
                {
                    TempRemovedMapPathes.Add(new Tuple<IAGV, MapPath>(waitingVehicle, path));
                    NotifyServiceHelper.WARNING($"{waitingVehicle.Name} 與在設備中的車輛({AvoidToVehicle.Name})互相等待! Close Path-{startPtOfPathClose.TagNumber}->{endPtOfPathClose.TagNumber}");
                }
            }
        }

    }
}

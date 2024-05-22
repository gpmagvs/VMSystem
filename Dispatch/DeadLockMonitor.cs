using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.Dispatch
{
    public class DeadLockMonitor
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
                        foreach (var item in WaitingConflicReleaseVehicles)
                        {
                            item.NavigationState.IsConflicSolving = true;
                        }
                        await DeadLockSolve(_deadLockVehicles);
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
        internal async void HandleVehicleStartWaitConflicSolve(object? sender, IAGV waitingVehicle)
        {
            var otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(waitingVehicle);
            //是否與停在設備中的車輛互相停等
            bool _IsWaitForVehicleAtWorkStationNear(out IEnumerable<IAGV> vehiclesAtWorkStation)
            {
                vehiclesAtWorkStation = otherVehicles.Where(v => v.currentMapPoint.StationType != MapPoint.STATION_TYPE.Normal);
                //vehiclesAtWorkStation = otherVehicles.Where(v => v.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                //                                     .Where(v => v.CurrentRunningTask().ActionType != ACTION_TYPE.None)
                //                                     .Where(v => v.NavigationState.IsWaitingForLeaveWorkStation);
                return vehiclesAtWorkStation.Any();
            }
            if (_IsWaitForVehicleAtWorkStationNear(out IEnumerable<IAGV> vehiclesAtWorkStation))
            {
                var AvoidToVehicle = vehiclesAtWorkStation.First();
                //要把等待中AGV到設備的路徑移除
                int tagOfEntryPointOfEq = AvoidToVehicle.currentMapPoint.TargetNormalPoints().First().TagNumber;
                MapPoint entryPoint = StaMap.Map.Points.Values.First(pt => pt.TagNumber == tagOfEntryPointOfEq);
                var _stored = TempRemovedMapPathes.FirstOrDefault(store => store.Item1 == waitingVehicle);
                if (_stored == null)
                {
                    int currentTag = waitingVehicle.currentMapPoint.TagNumber;
                    MapPoint currentPoint = StaMap.Map.Points.Values.First(pt => pt.TagNumber == currentTag);
                    int indexOfEntryPoint = StaMap.GetIndexOfPoint(entryPoint);
                    currentPoint?.Target?.Remove(indexOfEntryPoint, out _);
                    if (StaMap.TryRemovePathDynamic(currentPoint, entryPoint, out MapPath path))
                    {
                        TempRemovedMapPathes.Add(new Tuple<IAGV, MapPath>(waitingVehicle, path));
                        NotifyServiceHelper.WARNING($"{waitingVehicle.Name} 與在設備中的車輛({AvoidToVehicle.Name})互相等待! Close Path-{currentTag}->{tagOfEntryPointOfEq}");
                    }
                }
            }


            return;
            var executingOrderVehicles = VMSManager.AllAGV.Where(v => v.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                                                          .Where(v => v.CurrentRunningTask().ActionType == ACTION_TYPE.None)
                                                          .Where(v => !v.NavigationState.IsAvoidRaising);
            if (executingOrderVehicles.Count() < 2)
                return;
            var toAvoidVehicle = await DeadLockSolve(executingOrderVehicles.Take(2));
            if (toAvoidVehicle == null)
                return;
            if (toAvoidVehicle.main_state == clsEnums.MAIN_STATUS.RUN)
            {
                //await toAvoidVehicle.CurrentRunningTask().CycleStopRequestAsync();
            }
            else
            {

            }
        }


        /// <summary>
        /// 解決在EQ內互相等待
        /// </summary>
        public class clsLowPriorityVehicleWaitAtWorkStation : clsLowPriorityVehicleMove
        {
            public clsLowPriorityVehicleWaitAtWorkStation(IAGV _Vehicle, IAGV HightPriorityVehicle) : base(_Vehicle, HightPriorityVehicle)
            {
            }

            public override async Task<IAGV> StartSolve()
            {
                await Task.Delay(1000);
                if (!_LowProrityVehicle.NavigationState.IsWaitingForLeaveWorkStation)
                    return _LowProrityVehicle;

                _HightPriorityVehicle.NavigationState.LeaveWorkStationHighPriority = true;
                _HightPriorityVehicle.NavigationState.IsWaitingForLeaveWorkStation = false;
                NotifyServiceHelper.SUCCESS($"{_HightPriorityVehicle.Name}(優先) 與 {_LowProrityVehicle.Name} 車輛在設備內相互等待衝突已解決!");
                return _HightPriorityVehicle;
            }
        }

        public class clsLowPriorityVehicleMove
        {
            public IAGV _LowProrityVehicle { get; private set; }

            public IAGV _HightPriorityVehicle { get; private set; }

            public clsLowPriorityVehicleMove(IAGV _Vehicle, IAGV HightPriorityVehicle)
            {
                _LowProrityVehicle = _Vehicle;
                _HightPriorityVehicle = HightPriorityVehicle;
            }

            public virtual async Task<IAGV> StartSolve()
            {
                MapPoint StopMapPoint = DetermineStopMapPoint(out IEnumerable<MapPoint> pathToStopPoint);

                if (StopMapPoint == null)
                    return null;

                _LowProrityVehicle.NavigationState.AvoidPt = StopMapPoint;
                _LowProrityVehicle.NavigationState.IsAvoidRaising = true;
                _LowProrityVehicle.NavigationState.IsConflicSolving = false;
                _LowProrityVehicle.NavigationState.IsWaitingConflicSolve = false;
                _LowProrityVehicle.NavigationState.AvoidToVehicle = _HightPriorityVehicle;
                return _LowProrityVehicle;
            }

            private void NavigationState_OnAvoidPointCannotReach(object? sender, MapPoint e)
            {
                throw new NotImplementedException();
            }

            internal MapPoint DetermineStopMapPoint(out IEnumerable<MapPoint> pathToStopPoint)
            {
                try
                {
                    if (this._LowProrityVehicle.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING ||
                        this._HightPriorityVehicle.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                    {
                        pathToStopPoint = null;
                        return null;
                    }

                    pathToStopPoint = new List<MapPoint>();
                    //HPV=> High Priority Vehicle
                    MoveTaskDynamicPathPlanV2 currentTaskOfHPV = _HightPriorityVehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2;
                    MoveTaskDynamicPathPlanV2 currentTaskOfLPV = (_LowProrityVehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2);

                    if (currentTaskOfHPV == null)
                        return null;
                    var orderOfHPV = currentTaskOfHPV.OrderData;
                    var orderOfLPV = currentTaskOfLPV.OrderData;
                    MapPoint finalPtOfHPV = currentTaskOfHPV.finalMapPoint;
                    MapPoint finalPtOfLPV = currentTaskOfLPV.finalMapPoint;

                    MapPoint pointOfHPV = currentTaskOfHPV.AGVCurrentMapPoint;
                    List<MapPoint> cannotStopPoints = new List<MapPoint>();

                    try
                    {
                        var constrainsOfHPVPath = StaMap.Map.Points.Values.Where(pt => !pt.Enable).ToList();
                        var pathPredictOfHPV = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(pointOfHPV, finalPtOfHPV, constrainsOfHPVPath);
                        cannotStopPoints.AddRange(pathPredictOfHPV);
                        cannotStopPoints.AddRange(_GetConstrainsOfLPVStopPoint());
                        cannotStopPoints = cannotStopPoints.DistinctBy(pt => pt.TagNumber).ToList();

                        var canStopPointCandicates = StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && !pt.IsVirtualPoint && !cannotStopPoints.GetTagCollection().Contains(pt.TagNumber))
                                                                             .ToList();

                        IEnumerable<IEnumerable<MapPoint>> pathes = canStopPointCandicates.Select(pt => GetPathToStopPoint(pt)).ToList();
                        if (pathes.All(path => path == null))
                        {
                            throw new Exception();
                        }
                        else
                        {
                            var hpv = _HightPriorityVehicle;
                            var lpv = _LowProrityVehicle;
                            pathes = pathes.Where(path => path != null)
                                           .Where(path => path.Last().TagNumber != finalPtOfLPV.TagNumber)
                                           .Where(path => !path.Last().GetCircleArea(ref hpv, 1.5).IsIntersectionTo(finalPtOfHPV.GetCircleArea(ref hpv)))
                                           .Where(path => !path.Last().GetCircleArea(ref lpv, 1.5).IsIntersectionTo(hpv.AGVRotaionGeometry))
                                           .OrderBy(path => path.Last().CalculateDistance(finalPtOfLPV))
                                           //.OrderBy(path => path.Last().CalculateDistance(lpv.currentMapPoint))
                                           .Where(path => !path.IsPathConflicWithOtherAGVBody(lpv, out var c)).ToList();
                            pathToStopPoint = pathes.FirstOrDefault();
                            if (pathToStopPoint == null)
                                throw new Exception();
                            return pathToStopPoint.Last();
                        }
                        IEnumerable<MapPoint> GetPathToStopPoint(MapPoint stopPoint)
                        {
                            try
                            {
                                //var constrains = _GetConstrainsOfLPVStopPoint();
                                var constrains = StaMap.Map.Points.Values.Where(pt => !pt.Enable).ToList();
                                constrains.AddRange(_LowProrityVehicle.NavigationState.AvoidActionState.CannotReachHistoryPoints);
                                constrains.RemoveAll(pt => pt.TagNumber == _LowProrityVehicle.currentMapPoint.TagNumber);
                                var pathToStopPt = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(_LowProrityVehicle.currentMapPoint, stopPoint, constrains);
                                return pathToStopPt;
                            }
                            catch (Exception ex)
                            {
                                return null;
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        var _oriHPV = _HightPriorityVehicle;
                        var _oriLPV = _LowProrityVehicle;

                        _LowProrityVehicle = _oriHPV;
                        _HightPriorityVehicle = _oriLPV;
                        return DetermineStopMapPoint(out pathToStopPoint);
                    }

                    List<MapPoint> _GetConstrainsOfHPVFuturePath()
                    {
                        var constrains = DispatchCenter.GetConstrains(_HightPriorityVehicle, VMSManager.AllAGV.FilterOutAGVFromCollection(this._HightPriorityVehicle), finalPtOfHPV);
                        constrains.RemoveAll(pt => pt.TagNumber == this._LowProrityVehicle.currentMapPoint.TagNumber);
                        constrains.RemoveAll(pt => pt.TagNumber == this._HightPriorityVehicle.currentMapPoint.TagNumber);
                        return constrains;
                    }

                    List<MapPoint> _GetConstrainsOfLPVStopPoint()
                    {
                        var constrains = DispatchCenter.GetConstrains(_LowProrityVehicle, VMSManager.AllAGV.FilterOutAGVFromCollection(this._LowProrityVehicle), finalPtOfHPV);
                        constrains.Add(_LowProrityVehicle.currentMapPoint);
                        constrains.AddRange(StaMap.Map.Points.Values.Where(pt => !pt.Enable));
                        return constrains;

                    }

                    return finalPtOfHPV;
                }
                catch (Exception)
                {
                    pathToStopPoint = null;
                    return null;
                }
            }

        }

        public class clsYieldPathForWorkstationVehicle : clsLowPriorityVehicleMove
        {
            public clsYieldPathForWorkstationVehicle(IAGV _Vehicle, IAGV HightPriorityVehicle) : base(_Vehicle, HightPriorityVehicle)
            {
            }
        }

    }
}

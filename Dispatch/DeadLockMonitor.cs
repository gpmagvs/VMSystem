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
                DynamicPathClose(waitingVehicle, vehiclesAtWorkStation);
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

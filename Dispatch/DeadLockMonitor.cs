using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.DATABASE.DatabaseCaches;

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
        private IEnumerable<IAGV> WaitingConflicReleaseVehicles
        {
            get
            {
                return VMSManager.AllAGV.Where(v => IsWaitingConflicRegionRelease(v));
            }
        }

        public async Task StartAsync()
        {
            await Task.Delay(1);

            while (true)
            {
                await Task.Delay(1);
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

        private bool IsWaitingConflicRegionRelease(IAGV vehicle)
        {
            return vehicle.NavigationState.IsWaitingConflicSolve;
            //return vehicle.NavigationState.IsWaitingConflicSolve && !vehicle.NavigationState.IsConflicSolving;
        }

        private async Task DeadLockSolve(IEnumerable<IAGV> DeadLockVehicles)
        {
            //決定誰要先移動到避車點
            (IAGV lowPriorityVehicle, IAGV highPriorityVehicle) = DeterminPriorityOfVehicles(DeadLockVehicles);
            clsLowPriorityVehicleMove lowPriorityWork = new clsLowPriorityVehicleMove(lowPriorityVehicle, highPriorityVehicle);
            await lowPriorityWork.StartSolve();
            await Task.Delay(1000);

        }

        private (IAGV lowPriorityVehicle, IAGV highPriorityVehicle) DeterminPriorityOfVehicles(IEnumerable<IAGV> DeadLockVehicles)
        {
            var ordered = DeadLockVehicles.OrderBy(vehicle => CalculateWeights(vehicle));
            //var ordered = DeadLockVehicles.OrderBy(vehicle => (DateTime.Now - vehicle.NavigationState.StartWaitConflicSolveTime).TotalSeconds);
            return (ordered.First(), ordered.Last());
        }
        private int CalculateWeights(IAGV vehicle)
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



        public class clsLowPriorityVehicleMove
        {
            public IAGV Vehicle { get; private set; }

            public IAGV _HightPriorityVehicle { get; private set; }

            public clsLowPriorityVehicleMove(IAGV _Vehicle, IAGV HightPriorityVehicle)
            {
                Vehicle = _Vehicle;
                _HightPriorityVehicle = HightPriorityVehicle;
            }

            public async Task StartSolve()
            {
                MapPoint StopMapPoint = DetermineStopMapPoint(out IEnumerable<MapPoint> pathToStopPoint);

                if (StopMapPoint == null)
                    return;

                Vehicle.NavigationState.AvoidPt = StopMapPoint;
                Vehicle.NavigationState.IsAvoidRaising = true;
                Vehicle.NavigationState.IsConflicSolving = false;
                Vehicle.NavigationState.IsWaitingConflicSolve = false;
                Vehicle.NavigationState.AvoidToVehicle = _HightPriorityVehicle;
            }

            private MapPoint DetermineStopMapPoint(out IEnumerable<MapPoint> pathToStopPoint)
            {
                pathToStopPoint = new List<MapPoint>();
                //HPV=> High Priority Vehicle
                MoveTaskDynamicPathPlanV2 currentTaskOfHPV = _HightPriorityVehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2;
                var orderOfHPV = currentTaskOfHPV.OrderData;
                MapPoint finalPtOfHPV = currentTaskOfHPV.finalMapPoint;
                MapPoint pointOfHPV = currentTaskOfHPV.AGVCurrentMapPoint;
                List<MapPoint> cannotStopPoints = new List<MapPoint>();

                try
                {
                    var pathPredictOfHPV = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(pointOfHPV, finalPtOfHPV, _GetConstrainsOfHPVFuturePath());
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
                        pathes = pathes.Where(path => path != null)
                                       .Where(path => !path.Last().GetCircleArea(ref hpv, 1.5).IsIntersectionTo(finalPtOfHPV.GetCircleArea(ref hpv)))
                                       .OrderBy(path => path.Last().CalculateDistance(Vehicle.currentMapPoint))
                                       .Where(path => !path.IsPathConflicWithOtherAGVBody(Vehicle, out var c)).ToList();
                        pathToStopPoint = pathes.FirstOrDefault();
                        if (pathToStopPoint == null)
                            throw new Exception();
                        return pathToStopPoint.Last();
                    }
                    IEnumerable<MapPoint> GetPathToStopPoint(MapPoint stopPoint)
                    {
                        try
                        {
                            var constrains = _GetConstrainsOfLPVStopPoint();
                            constrains.RemoveAll(pt => pt.TagNumber == Vehicle.currentMapPoint.TagNumber);
                            var pathToStopPt = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(Vehicle.currentMapPoint, stopPoint, constrains);
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
                    var _oriLPV = Vehicle;

                    Vehicle = _oriHPV;
                    _HightPriorityVehicle = _oriLPV;
                    return DetermineStopMapPoint(out pathToStopPoint);
                }

                List<MapPoint> _GetConstrainsOfHPVFuturePath()
                {
                    var constrains = DispatchCenter.GetConstrains(_HightPriorityVehicle, VMSManager.AllAGV.FilterOutAGVFromCollection(this._HightPriorityVehicle), finalPtOfHPV);
                    constrains.RemoveAll(pt => pt.TagNumber == this.Vehicle.currentMapPoint.TagNumber);
                    constrains.RemoveAll(pt => pt.TagNumber == this._HightPriorityVehicle.currentMapPoint.TagNumber);
                    return constrains;
                }

                List<MapPoint> _GetConstrainsOfLPVStopPoint()
                {
                    var constrains = DispatchCenter.GetConstrains(Vehicle, VMSManager.AllAGV.FilterOutAGVFromCollection(this.Vehicle), finalPtOfHPV);
                    constrains.Add(Vehicle.currentMapPoint);
                    constrains.AddRange(StaMap.Map.Points.Values.Where(pt => !pt.Enable));
                    return constrains;

                }

                return finalPtOfHPV;
            }

        }
    }
}

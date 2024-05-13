using AGVSystemCommonNet6;
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
            //var ordered = DeadLockVehicles.OrderBy(vehicle => CalculateWeights(vehicle));
            var ordered = DeadLockVehicles.OrderBy(vehicle => (DateTime.Now - vehicle.NavigationState.StartWaitConflicSolveTime).TotalSeconds);
            return (ordered.First(), ordered.Last());
        }

        private int CalculateWeights(IAGV vehicle)
        {
            return 0;
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



                return;


                //await Vehicle.CurrentRunningTask().SendCancelRequestToAGV();
                //while (Vehicle.main_state == clsEnums.MAIN_STATUS.RUN)
                //{
                //    if (Vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE || Vehicle.CurrentRunningTask().IsTaskCanceled)
                //        return;
                //    await Task.Delay(20);
                //}
                //await StaMap.UnRegistPointsOfAGVRegisted(_HightPriorityVehicle);
                //StaMap.RegistPoint(Vehicle.Name, pathToStopPoint, out string msg);
                //await Vehicle.CurrentRunningTask().SendTaskToAGV(new AGVSystemCommonNet6.AGVDispatch.Messages.clsTaskDownloadData
                //{
                //    Action_Type = AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None,
                //    Destination = StopMapPoint.TagNumber,
                //    Task_Name = "TAF",
                //    Task_Sequence = 0,
                //    Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathToStopPoint.ToList())
                //});

                //Vehicle.NavigationState.UpdateNavigationPoints(pathToStopPoint);
                //while (Vehicle.main_state != clsEnums.MAIN_STATUS.RUN)
                //{
                //    if (Vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE || Vehicle.CurrentRunningTask().IsTaskCanceled)
                //        return;
                //    await Task.Delay(200);
                //}
                //while (Vehicle.main_state == clsEnums.MAIN_STATUS.RUN || Vehicle.currentMapPoint.TagNumber != StopMapPoint.TagNumber)
                //{
                //    if (Vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE || Vehicle.CurrentRunningTask().IsTaskCanceled)
                //        return;
                //    await Task.Delay(220);
                //}

                //Vehicle.NavigationState.ResetNavigationPoints();
                //await StaMap.UnRegistPointsOfAGVRegisted(Vehicle);
                //await Task.Delay(200);
                //await _HightPriorityVehicle.CurrentRunningTask().SendCancelRequestToAGV();


                //while (_HightPriorityVehicle.main_state == clsEnums.MAIN_STATUS.RUN)
                //{
                //    if (_HightPriorityVehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE || _HightPriorityVehicle.CurrentRunningTask().IsTaskCanceled)
                //        return;
                //    await Task.Delay(20);
                //}


                //_HightPriorityVehicle.NavigationState.IsConflicSolving = false;

                //while (_HightPriorityVehicle.main_state != clsEnums.MAIN_STATUS.RUN)
                //{
                //    Vehicle.NavigationState.ResetNavigationPoints();
                //    if (Vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE || Vehicle.CurrentRunningTask().IsTaskCanceled)
                //        return;
                //    await Task.Delay(200);
                //    (Vehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"Wait {_HightPriorityVehicle.Name} Start Move");

                //}

                //await Task.Delay(200);
                //Vehicle.NavigationState.IsConflicSolving = false;
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
                                       .OrderBy(path => path.Last().CalculateDistance(Vehicle.currentMapPoint))
                                       .Where(path => !path.Last().GetCircleArea(ref hpv).IsIntersectionTo(hpv.AGVRotaionGeometry)).ToList();
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

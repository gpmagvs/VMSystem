using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.Dispatch.YieldActions
{
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
                if (_LowProrityVehicle.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING ||
                    _HightPriorityVehicle.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                {
                    pathToStopPoint = null;
                    return null;
                }

                pathToStopPoint = new List<MapPoint>();
                //HPV=> High Priority Vehicle
                MoveTaskDynamicPathPlanV2 currentTaskOfHPV = _HightPriorityVehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2;
                MoveTaskDynamicPathPlanV2 currentTaskOfLPV = _LowProrityVehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2;

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
                            constrains.AddRange(_LowProrityVehicle.NavigationState.AvoidActionState.CannotReachHistoryPoints.Values);
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
                    var constrains = DispatchCenter.GetConstrains(_HightPriorityVehicle, VMSManager.AllAGV.FilterOutAGVFromCollection(_HightPriorityVehicle), finalPtOfHPV);
                    constrains.RemoveAll(pt => pt.TagNumber == _LowProrityVehicle.currentMapPoint.TagNumber);
                    constrains.RemoveAll(pt => pt.TagNumber == _HightPriorityVehicle.currentMapPoint.TagNumber);
                    return constrains;
                }

                List<MapPoint> _GetConstrainsOfLPVStopPoint()
                {
                    var constrains = DispatchCenter.GetConstrains(_LowProrityVehicle, VMSManager.AllAGV.FilterOutAGVFromCollection(_LowProrityVehicle), finalPtOfHPV);
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

}

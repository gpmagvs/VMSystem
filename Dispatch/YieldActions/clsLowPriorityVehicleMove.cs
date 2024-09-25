using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using WebSocketSharp;

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
            {
                return null;
            }

            _LowProrityVehicle.NavigationState.IsConflicSolving = false;
            _LowProrityVehicle.NavigationState.IsWaitingConflicSolve = false;

            _LowProrityVehicle.NavigationState.AvoidActionState.AvoidAction = AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None;
            _LowProrityVehicle.NavigationState.AvoidActionState.AvoidPt = StopMapPoint;
            _LowProrityVehicle.NavigationState.AvoidActionState.IsAvoidRaising = true;
            _LowProrityVehicle.NavigationState.AvoidActionState.AvoidToVehicle = _HightPriorityVehicle;
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
                TaskBase currentTaskOfHPV = _HightPriorityVehicle.CurrentRunningTask();
                TaskBase currentTaskOfLPV = _LowProrityVehicle.CurrentRunningTask();

                if (currentTaskOfHPV == null)
                    return null;
                var orderOfHPV = currentTaskOfHPV.OrderData;
                var orderOfLPV = currentTaskOfLPV.OrderData;
                MapPoint finalPtOfHPV = StaMap.GetPointByTagNumber(currentTaskOfHPV.DestineTag);
                MapPoint finalPtOfLPV = StaMap.GetPointByTagNumber(currentTaskOfLPV.DestineTag);

                MapPoint pointOfHPV = currentTaskOfHPV.AGVCurrentMapPoint;
                List<MapPoint> cannotStopPoints = new List<MapPoint>();

                try
                {
                    var constrainsOfHPVPath = StaMap.Map.Points.Values.Where(pt => !pt.Enable).ToList();
                    var pathPredictOfHPV = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(pointOfHPV, finalPtOfHPV, constrainsOfHPVPath);
                    cannotStopPoints.AddRange(pathPredictOfHPV);
                    cannotStopPoints.AddRange(_GetConstrainsOfLPVStopPoint());
                    cannotStopPoints.Add(_LowProrityVehicle.currentMapPoint);
                    cannotStopPoints.AddRange(_LowProrityVehicle.GetCanNotReachMapPoints()); //加入不可抵達的點位們
                    cannotStopPoints = cannotStopPoints.Where(pt => !pt.IsAvoid).DistinctBy(pt => pt.TagNumber).ToList();

                    var canStopPointCandicates = StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && !pt.IsVirtualPoint && !cannotStopPoints.GetTagCollection().Contains(pt.TagNumber))
                                                                         .ToList();
                    canStopPointCandicates = canStopPointCandicates.Where(pt => pt.TagNumber != _LowProrityVehicle.currentMapPoint.TagNumber).ToList();
                    // Find Avoid Point In Low Priority Vehicle current Region   from canStopPointCandicates
                    MapRegion regionOfLPV = _LowProrityVehicle.currentMapPoint.GetRegion();

                    //List<MapPoint> avaliableAvoidPoints = canStopPointCandicates.Where(pt => pt.IsAvoid && IsPointReachable(pt)).ToList();



                    //avaliableAvoidPoints = avaliableAvoidPoints.OrderBy(pt => pt.CalculateDistance(_LowProrityVehicle.currentMapPoint)).ToList();

                    //if (avaliableAvoidPoints.Any())
                    //{
                    //    IEnumerable<IEnumerable<MapPoint>> PathToAvoidPoints = avaliableAvoidPoints.Select(pt => GetPathToStopPoint(pt)).ToList();
                    //    IEnumerable<MapPoint> pathToAvoidPoint = PathToAvoidPoints.FirstOrDefault(path => path != null);
                    //    if (pathToAvoidPoint != null)
                    //        return pathToAvoidPoint.Last();
                    //    else
                    //        return null;
                    //}

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
                                       .OrderBy(path => path.TotalTravelDistance())
                                       //.OrderBy(path => path.Last().CalculateDistance(lpv.currentMapPoint))
                                       .Where(path => !path.IsPathConflicWithOtherAGVBody(lpv, out var c)).ToList();



                        MapRegion currentRegionOfLPV = lpv.currentMapPoint.GetRegion();
                        MapRegion currentRegionOfHPV = hpv.currentMapPoint.GetRegion();
                        if (!currentRegionOfLPV.Name.IsNullOrEmpty() && currentRegionOfLPV.MaxVehicleCapacity < 2)
                        {
                            pathToStopPoint = pathes.FirstOrDefault(path => _isStopPointNotInLPVAndHPVCurrentRegion(path.Last()));

                            bool _isStopPointNotInLPVAndHPVCurrentRegion(MapPoint _stopPointCandicate)
                            {
                                MapRegion regionOfStopPoint = _stopPointCandicate.GetRegion();
                                return regionOfStopPoint.Name != currentRegionOfLPV.Name && regionOfStopPoint.Name != currentRegionOfHPV.Name;
                            }
                        }
                        else
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

                    bool IsPointReachable(MapPoint point)
                    {
                        IEnumerable<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(_LowProrityVehicle);
                        bool pointOcuupy = otherVehicles.Any(agv => agv.currentMapPoint.TagNumber == point.TagNumber || agv.CurrentRunningTask().OrderData?.To_Station_Tag == point.TagNumber);
                        IAGV _lpVehile = _LowProrityVehicle;
                        if (_lpVehile.NavigationState.AvoidActionState.CannotReachHistoryPoints.Keys.Contains(point.TagNumber))
                            return false;
                        bool pointConflicToOtherAGV = otherVehicles.Any(agv => agv.NavigationState.NextPathOccupyRegions.Any(rct => rct.IsIntersectionTo(point.GetCircleArea(ref _lpVehile))));
                        return !pointOcuupy && !pointConflicToOtherAGV;
                    }
                }
                catch (Exception ex)
                {
                    return null;
                    //var _oriHPV = _HightPriorityVehicle;
                    //var _oriLPV = _LowProrityVehicle;
                    //_LowProrityVehicle = _oriHPV;
                    //_HightPriorityVehicle = _oriLPV;
                    //return DetermineStopMapPoint(out pathToStopPoint);
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

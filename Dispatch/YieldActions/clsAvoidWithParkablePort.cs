using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.Dispatch.YieldActions
{
    public class clsAvoidWithParkablePort : clsLowPriorityVehicleMove
    {
        public clsAvoidWithParkablePort(IAGV _Vehicle, IAGV HightPriorityVehicle) : base(_Vehicle, HightPriorityVehicle)
        {

        }
        public override async Task<IAGV> StartSolve()
        {
            //要選一個停車點 
            MapPoint optimizeParkPort = null;
            try
            {
                IEnumerable<MapPoint> parkablePortPointsInRegion = _LowProrityVehicle.currentMapPoint.GetRegion().GetParkablePointOfRegion(_LowProrityVehicle);
                if (parkablePortPointsInRegion.Any())
                {
                    parkablePortPointsInRegion = parkablePortPointsInRegion.Where(pt => _IsPassableWhenMoveTo(pt)); //過濾出移動過去時不會與其他AGV衝突的可停車點。
                    var orderedByDistance = parkablePortPointsInRegion.ToDictionary(pt => pt, pt => pt.CalculateDistance(_LowProrityVehicle.states.Coordination))
                                                                      .OrderBy(pt => pt.Value); //找離目前位置最近的停車點。

                    optimizeParkPort = orderedByDistance.FirstOrDefault().Key;
                    //goalPortPoint:非一般點位的可停車點
                    bool _IsPassableWhenMoveTo(MapPoint goalPortPoint)
                    {
                        if (goalPortPoint.StationType == MapPoint.STATION_TYPE.Normal)
                            return true; //
                        try
                        {
                            //先取得二次定位點
                            MapPoint secondaryPt = goalPortPoint.TargetNormalPoints().FirstOrDefault();
                            if (secondaryPt != null)
                            {
                                List<MapPoint> constrains = VMSManager.AllAGV.FilterOutAGVFromCollection(_LowProrityVehicle)
                                                                             .Select(vehicle => vehicle.currentMapPoint)
                                                                             .ToList();
                                IEnumerable<MapPoint> path = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(_LowProrityVehicle.currentMapPoint, secondaryPt, constrains);
                                if (path.Count() == 0)
                                    return false;
                                //計算干涉與註冊狀況
                                List<MapRectangle> agvBodyCoveringWhenMoveToGoal= Tools.GetPathRegionsWithRectangle(path.ToList(), _LowProrityVehicle.options.VehicleWidth / 100.0, _LowProrityVehicle.options.VehicleLength / 100.0);
                                return path.All(pt => !pt.IsRegisted(_LowProrityVehicle)) && !agvBodyCoveringWhenMoveToGoal.IsAnyVehicleConflicTo(_LowProrityVehicle);
                            }
                            else
                                return false;
                        }
                        catch (NoPathForNavigatorException ex)
                        {
                            //找不到路過去也視為衝突
                            return false;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }

                }
            }
            catch (Exception)
            {
                optimizeParkPort = DeadLockMonitor.GetParkableStationOfCurrentRegion(_LowProrityVehicle);
            }
            if (optimizeParkPort == null)
                return null;
            _LowProrityVehicle.NavigationState.IsConflicSolving = false;
            _LowProrityVehicle.NavigationState.IsWaitingConflicSolve = false;
            _LowProrityVehicle.NavigationState.AvoidActionState.AvoidAction = AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Park;
            _LowProrityVehicle.NavigationState.AvoidActionState.AvoidPt = optimizeParkPort;
            _LowProrityVehicle.NavigationState.AvoidActionState.IsAvoidRaising = true;
            _LowProrityVehicle.NavigationState.AvoidActionState.AvoidToVehicle = _HightPriorityVehicle;
            return _LowProrityVehicle;
        }
    }
}

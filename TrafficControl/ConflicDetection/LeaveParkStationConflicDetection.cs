using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using WebSocketSharp;
using static VMSystem.AGV.TaskDispatch.Tasks.MoveTaskDynamicPathPlanV2;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveParkStationConflicDetection : LeaveWorkstationConflicDetection
    {
        MapRegion entryPtRegion => DetectPoint.GetRegion();
        public LeaveParkStationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
        }


        public override clsConflicDetectResultWrapper Detect()
        {
            if(!RegionManager.IsRegionEnterable(this.AGVToDetect,entryPtRegion))
            {
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, "")
                {
                    ConflicStatusCode = CONFLIC_STATUS_CODE.CONFLIC_TO_OTHER_NAVIGATING_PATH,
                    Message = $"等待其他車輛通過 {entryPtRegion.Name} 區域"
                };
            }

            return base.Detect();
        }

        // create a new method here
        public bool IsAnyVehiclePassRegionPassible(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();

            if (entryPtRegion.RegionType == MapRegion.MAP_REGION_TYPE.UNKNOWN || entryPtRegion.Name.IsNullOrEmpty())
                return false;

            //conflicAGVList = OtherAGV.Where(_agv => _agv.AGVRotaionGeometry.IsIntersectionTo(RectangleOfDetectPoint))
            var _OtherRunningAGV = OtherAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                                            .ToList();

            // search PassRegionPassible
            conflicAGVList = _OtherRunningAGV.Where(agv => _PassRegionPassible(agv))
                                             .ToList();


            bool _PassRegionPassible(IAGV agv)
            {
                try
                {
                    clsRegionControlState regionControlState = agv.NavigationState.RegionControlState;
                    if (regionControlState.IsWaitingForEntryRegion && regionControlState.NextToGoRegion.Name == entryPtRegion.Name)
                    {
                        return true;
                    }

                    if (agv.currentMapPoint.StationType != MapPoint.STATION_TYPE.Normal)
                        return false;


                    if (agv.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN && agv.NavigationState.IsWaitingConflicSolve)
                        return false;

                    TaskBase currentTask = agv.CurrentRunningTask();
                    MapPoint nextDestinePt = StaMap.GetPointByTagNumber(agv.CurrentRunningTask().DestineTag);

                    bool isAgvLDULDRunning = agv.CurrentRunningTask().ActionType != AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None && agv.NavigationState.WorkStationMoveState == VehicleNavigationState.WORKSTATION_MOVE_STATE.BACKWARDING;
                    IEnumerable<MapPoint> _optimizedPath = new List<MapPoint>();
                    if (isAgvLDULDRunning)
                    {
                        _optimizedPath = agv.CurrentRunningTask().TaskDonwloadToAGV.Homing_Trajectory
                                                                                   .Reverse()
                                                                                   .Select(pt => StaMap.GetPointByTagNumber(pt.Point_ID));
                    }
                    else
                        _optimizedPath = LowLevelSearch.GetOptimizedMapPoints(agv.currentMapPoint, nextDestinePt, null);
                    var regions = _optimizedPath.GetRegions().ToList();
                    if (!regions.Any(rg => rg.Name == entryPtRegion.Name))
                        return false;

                    if (regions.Last().Name == entryPtRegion.Name)
                        return false;
                    else
                    {
                        if (agv.NavigationState.NextNavigtionPoints.Last().GetRegion().Name != entryPtRegion.Name)
                            return false;
                    }


                    return true;
                }
                catch (Exception ex)
                {
                    return true;
                }
            }

            return conflicAGVList.Any() && conflicAGVList.Count() + 1 >= entryPtRegion.MaxVehicleCapacity;
        }

    }
}

using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.Dispatch.Regions;
using WebSocketSharp;
using static VMSystem.AGV.TaskDispatch.Tasks.MoveTaskDynamicPathPlanV2;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveParkStationConflicDetection : LeaveWorkstationConflicDetection
    {
        MapRegion entryPtRegion => DetectPoint.GetRegion(StaMap.Map);
        public LeaveParkStationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
        }


        public override clsConflicDetectResultWrapper Detect()
        {
            if (IsAnyVehiclePassRegionPassible(out List<IAGV> confliAGVList))
            {
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, "")
                {
                    ConflicToAGVList = confliAGVList,
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
           
            if (entryPtRegion.Name.IsNullOrEmpty())
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
                    MapPoint nextDestinePt = StaMap.GetPointByTagNumber(agv.CurrentRunningTask().DestineTag);
                    var _optimizedPath = LowLevelSearch.GetOptimizedMapPoints(agv.currentMapPoint, nextDestinePt, null);
                    var regions = _optimizedPath.GetRegions().ToList();
                    if (!regions.Any(rg => rg.Name == entryPtRegion.Name))
                        return false;

                    if (regions.Last().Name == entryPtRegion.Name)
                        return false;
                    return true;
                }
                catch (Exception ex)
                {
                    return true;
                }
            }

            return conflicAGVList.Any();
        }

    }
}

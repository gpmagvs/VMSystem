using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveWorkstationConflicDetection : ConflicDetectionBase
    {
        public override clsTrafficControlParameters.clsVehicleGeometryExpand GeometryExpand { get; set; } = TrafficControlCenter.TrafficControlParameters.VehicleGeometryExpands.LeaveWorkStationGeoExpand;

        public LeaveWorkstationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
        }

        public override clsConflicDetectResultWrapper Detect()
        {
            clsConflicDetectResultWrapper baseDetectResult = base.Detect();

            return baseDetectResult;
        }

        public override bool IsConflicToOtherVehicleRotaionBody(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();
            MapRectangle RectangleOfDetectPoint = GetRectangleOfDetectPoint();
            conflicAGVList = OtherAGV.Where(_agv => _agv.AGVRotaionGeometry.IsIntersectionTo(RectangleOfDetectPoint))
                                     .ToList();

            return conflicAGVList.Any();
        }
    }
}

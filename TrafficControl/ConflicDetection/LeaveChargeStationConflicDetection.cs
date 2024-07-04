using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveChargeStationConflicDetection : ConflicDetectionBase
    {
        public override clsTrafficControlParameters.clsVehicleGeometryExpand GeometryExpand { get; set; } = TrafficControlCenter.TrafficControlParameters.VehicleGeometryExpands.LeaveChargeStationGeoExpand;
        public LeaveChargeStationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
        }

        public override clsConflicDetectResultWrapper Detect()
        {
            clsConflicDetectResultWrapper baseDetectResult = base.Detect();

            return baseDetectResult;
        }

        protected override IEnumerable<IAGV> GetOtherVehicles()
        {
            IEnumerable<IAGV> baseFiltered = base.GetOtherVehicles();
            var chargeDetetionAGVList = baseFiltered.Where(agv => agv.currentMapPoint.StationType != MapPoint.STATION_TYPE.Charge).ToList();
            Console.WriteLine($"chargeDetetionAGVList count:{chargeDetetionAGVList.Count}");
            return chargeDetetionAGVList;
            //return baseFiltered.SkipWhile(agv => agv.currentMapPoint.IsCharge && agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
        }

    }
}

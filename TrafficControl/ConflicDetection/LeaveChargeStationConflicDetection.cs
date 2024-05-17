using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveChargeStationConflicDetection : ConflicDetectionBase
    {
        public LeaveChargeStationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
            base.AGVLengthExpandRatio = 2;
        }

        public override clsConflicDetectResultWrapper Detect()
        {
            clsConflicDetectResultWrapper baseDetectResult = base.Detect();

            return baseDetectResult;
        }

        protected override IEnumerable<IAGV> GetOtherVehicles()
        {
            IEnumerable<IAGV> baseFiltered = base.GetOtherVehicles();
            return baseFiltered.SkipWhile(agv => agv.currentMapPoint.IsCharge && agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
        }

    }
}

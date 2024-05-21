using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveWorkstationConflicDetection : ConflicDetectionBase
    {
        public LeaveWorkstationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
            AGVLengthExpandRatio = 1.8;
            AGVWidthExpandRatio = 1.1;
        }

        public override clsConflicDetectResultWrapper Detect()
        {
            clsConflicDetectResultWrapper baseDetectResult = base.Detect();

            return baseDetectResult;
        }
    }
}

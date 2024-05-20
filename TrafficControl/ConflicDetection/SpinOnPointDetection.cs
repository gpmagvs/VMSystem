using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class SpinOnPointDetection : ConflicDetectionBase
    {
        public override double AGVLengthExpandRatio { get; set; } = 2;
        public override double AGVWidthExpandRatio { get; set; } = 2;
        public SpinOnPointDetection(MapPoint DetectPoint, double ThetaOfPrediction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPrediction, AGVToDetect)
        {

        }

        public override clsConflicDetectResultWrapper Detect()
        {
            //base.Detect();
            return base.Detect();
        }
    }
}

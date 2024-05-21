using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;

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
            if (IsConflicToOtherVehiclesFinalStopPoint(out List<IAGV> conflicAGVList))
            {
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, $"Cannot Spin-> Will Conflic To {conflicAGVList.GetNames()}")
                {
                    ConflicStatusCode = CONFLIC_STATUS_CODE.CONFLIC_TO_OTHER_NAVIGATING_PATH
                };
            }
            return base.Detect();
        }
        public bool IsConflicToOtherVehiclesFinalStopPoint(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();
            MapRectangle RectangleOfDetectPoint = GetRectangleOfDetectPoint();
            var executingTaskVehicles = OtherAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
            Dictionary<IAGV, List<MapRectangle>> remainPathCoflicState = executingTaskVehicles.ToDictionary(agv => agv, agv => agv.NavigationState.NextPathOccupyRegions.Where(rct => rct.IsIntersectionTo(RectangleOfDetectPoint)).ToList());
            conflicAGVList.AddRange(remainPathCoflicState.Where(kp => kp.Value.Count != 0).Select(kp => kp.Key));
            Dictionary<IAGV, bool> finalDestineNearState = executingTaskVehicles.ToDictionary(agv => agv, agv => _IsFinalDestineTooNear(agv));
            conflicAGVList.AddRange(finalDestineNearState.Where(kp => kp.Value).Select(kp => kp.Key));
            return conflicAGVList.Any();

            bool _IsFinalDestineTooNear(IAGV agv)
            {
                double distanceThres = this.AGVToDetect.AGVRotaionGeometry.RotationRadius * 3;
                return (agv.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).finalMapPoint.CalculateDistance(this.AGVToDetect.states.Coordination) <= distanceThres;
            }
        }
    }
}

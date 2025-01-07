using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class SpinOnPointDetection : ConflicDetectionBase
    {
        public override clsTrafficControlParameters.clsVehicleGeometryExpand GeometryExpand { get; set; } = TrafficControlCenter.TrafficControlParameters.VehicleGeometryExpands.SpinOnPointGeoExpand;
        public SpinOnPointDetection(MapPoint DetectPoint, double ThetaOfPrediction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPrediction, AGVToDetect)
        {

        }
        protected override IEnumerable<IAGV> GetOtherVehicles()
        {
            var _otherVehicles = base.GetOtherVehicles();
            return _otherVehicles.Where(agv => agv.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None);
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

            if (IsOtherVehiclesCurrentLocationSoFar())
            {
                NotifyServiceHelper.INFO($"{AGVToDetect.Name} Spin Directly because Other Vehicles is so far(so good)");
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.OK, "");
            }
            return base.Detect();
        }
        public bool IsConflicToOtherVehiclesFinalStopPoint(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();
            MapRectangle RectangleOfDetectPoint = GetRotationRectangeOfDetectPoint();
            var executingTaskVehicles = OtherAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
            Dictionary<IAGV, List<MapRectangle>> remainPathCoflicState = executingTaskVehicles.ToDictionary(agv => agv, agv => agv.NavigationState.NextPathOccupyRegions.Where(rct => rct.IsIntersectionTo(RectangleOfDetectPoint)).ToList());
            conflicAGVList.AddRange(remainPathCoflicState.Where(kp => kp.Value.Count != 0).Select(kp => kp.Key));
            //Dictionary<IAGV, bool> finalDestineNearState = executingTaskVehicles.ToDictionary(agv => agv, agv => _IsFinalDestineTooNear(agv));
            //conflicAGVList.AddRange(finalDestineNearState.Where(kp => kp.Value).Select(kp => kp.Key));
            return conflicAGVList.Any();

            bool _IsFinalDestineTooNear(IAGV agv)
            {
                double distanceThres = this.AGVToDetect.AGVRotaionGeometry.RotationRadius * 3;
                return (agv.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).finalMapPoint.CalculateDistance(this.AGVToDetect.states.Coordination) <= distanceThres;
            }
        }
    }
}

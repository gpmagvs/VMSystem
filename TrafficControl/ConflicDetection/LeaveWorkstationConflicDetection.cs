using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public class LeaveWorkstationConflicDetection : ConflicDetectionBase
    {
        public override clsTrafficControlParameters.clsVehicleGeometryExpand GeometryExpand { get; set; } = TrafficControlCenter.TrafficControlParameters.VehicleGeometryExpands.LeaveWorkStationGeoExpand;

        public LeaveWorkstationConflicDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
        }
        protected override IEnumerable<IAGV> GetOtherVehicles()
        {
            return base.GetOtherVehicles().Where(agv => _IsOnMainPath(agv) || _IsOnWorkStationBackwarding(agv));
            bool _IsOnMainPath(IAGV vehicle)
            {
                return vehicle.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal;
            }

            bool _IsOnWorkStationBackwarding(IAGV vehicle)
            {
                try
                {
                    // 快速失敗檢查：如果不在執行狀態或沒有運行中的任務，直接返回 false
                    if (vehicle?.taskDispatchModule?.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING ||
                        vehicle.CurrentRunningTask() is not TaskBase runningTask)
                    {
                        return false;
                    }

                    // 特殊情況處理：避讓停車邏輯
                    if (runningTask.ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None &&
                        runningTask.subStage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.AvoidPath_Park &&
                        vehicle.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal)
                    {
                        return false;
                    }
                    // 檢查車輛是否處於後退狀態
                    return vehicle.NavigationState.WorkStationMoveState == VehicleNavigationState.WORKSTATION_MOVE_STATE.BACKWARDING;
                }
                catch (Exception ex)
                {
                    logger.Error($"檢查車輛狀態時發生錯誤: {ex.Message}");
                    return false;
                }
            }
        }
        public override clsConflicDetectResultWrapper Detect()
        {
            clsConflicDetectResultWrapper baseDetectResult = base.Detect();
            if (baseDetectResult.Result != DETECTION_RESULT.OK && _IsConflicAGVEntryWorkStationCurrent(baseDetectResult))
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.OK, "");
            return baseDetectResult;


            bool _IsConflicAGVEntryWorkStationCurrent(clsConflicDetectResultWrapper baseDetectResult)
            {
                return baseDetectResult.ConflicToAGVList.Any(agv => agv.CurrentRunningTask().ActionType != AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None
                                                                    && agv.NavigationState.WorkStationMoveState == VehicleNavigationState.WORKSTATION_MOVE_STATE.FORWARDING);
            }
        }

        public override bool IsConflicToOtherVehicleRotaionBody(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();
            MapRectangle RectangleOfDetectPoint = GetRectangleOfDetectPoint();

            conflicAGVList = OtherAGV.Where(_agv => _agv.AGVRotaionGeometry.IsIntersectionTo(RectangleOfDetectPoint))
                                     .ToList();
            var LdUldActingVehicles = OtherAGV.Where(agv => agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN && agv.CurrentRunningTask().ActionType != AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None && agv.NavigationState.WorkStationMoveState == VehicleNavigationState.WORKSTATION_MOVE_STATE.BACKWARDING);

            //設備進入點
            if (LdUldActingVehicles.Any())
            {
                IEnumerable<IAGV> conflicAtEntrys = LdUldActingVehicles.ToDictionary(agv => agv, agv => agv.CurrentRunningTask().TaskDonwloadToAGV.Homing_Trajectory.First().Point_ID)
                                    .ToDictionary(kp => kp.Key, kp => _GetEntryPointCircleArea(kp.Key, kp.Value))
                                    .Where(kp => kp.Value.IsIntersectionTo(RectangleOfDetectPoint))
                                    .Select(kp => kp.Key);
                if (LdUldActingVehicles.Any())
                {
                    conflicAGVList.AddRange(conflicAtEntrys);
                }
            }

            conflicAGVList = conflicAGVList.DistinctBy(agv => agv.Name).ToList();
            return conflicAGVList.Any();
        }

        private MapCircleArea _GetEntryPointCircleArea(IAGV agv, int tag)
        {
            return StaMap.GetPointByTagNumber(tag).GetCircleArea(ref agv, 1);
        }
    }
}

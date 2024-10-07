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
            var chargeDetetionAGVList = baseFiltered.Where(agv => !_IsAGVReadyMoveToPort(agv) && agv.currentMapPoint.StationType != MapPoint.STATION_TYPE.Charge).ToList();
            Console.WriteLine($"chargeDetetionAGVList count:{chargeDetetionAGVList.Count}");
            return chargeDetetionAGVList;
            //return baseFiltered.SkipWhile(agv => agv.currentMapPoint.IsCharge && agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);

            bool _IsAGVReadyMoveToPort(IAGV agv)
            {
                if (agv.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                    return false;

                if (agv.CurrentRunningTask().ActionType != AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Load &&
                   agv.CurrentRunningTask().ActionType != AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Unload)
                    return false;

                return agv.NavigationState.WorkStationMoveState == VehicleNavigationState.WORKSTATION_MOVE_STATE.FORWARDING;
            }
        }

    }
}

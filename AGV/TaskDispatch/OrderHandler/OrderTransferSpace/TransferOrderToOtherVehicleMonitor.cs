using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace
{
    public class TransferOrderToOtherVehicleMonitor : OrderTransfer
    {

        /// <summary>
        /// 其他車輛
        /// </summary>
        private List<IAGV> OtherVehicles => VMSManager.AllAGV.FilterOutAGVFromCollection(orderOwner).ToList();
        private readonly MapPoint TargetWorkStationMapPoint;

        public TransferOrderToOtherVehicleMonitor(IAGV orderOwner, clsTaskDto order) : base(orderOwner, order)
        {
            if (order.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Carry)
                TargetWorkStationMapPoint = StaMap.GetPointByTagNumber(order.From_Station_Tag);
            else
                TargetWorkStationMapPoint = StaMap.GetPointByTagNumber(order.To_Station_Tag);
        }

        public override bool TryFindBetterVehicle(out IAGV betterVehicle)
        {
            betterVehicle = null;
            //評估是否有其他車輛當前位置
            double distanceToWorkStationOfOwner = TargetWorkStationMapPoint.CalculateDistance(orderOwner.states.Coordination);

            var moreNearToGoalVehicles = OtherVehicles.ToDictionary(vehicle => vehicle, vehicle => TargetWorkStationMapPoint.CalculateDistance(vehicle.states.Coordination))
                         .OrderBy(kp => kp.Value)
                         .Where(kp => kp.Value < distanceToWorkStationOfOwner)
                         .ToDictionary(kp => kp.Key, kp => kp.Value);
            //過濾出正在IDLE 或 正在執行充電任務訂單的車輛
            var idleOrChargingVehicles = moreNearToGoalVehicles.Where(kp => kp.Key.online_state == clsEnums.ONLINE_STATE.ONLINE) //上線中車輛
                                                               .Where(kp => IsVehicleNoOrder(kp.Key) || IsVehicleExecutingChargeTask(kp.Key)) //執行充電任務中 or 空閒中車輛
                                                                .ToList();
            if (idleOrChargingVehicles.Any())
                betterVehicle = idleOrChargingVehicles.First().Key;
            return betterVehicle != null;
        }


        private bool IsVehicleExecutingChargeTask(IAGV vehicle)
        {
            if (IsVehicleNoOrder(vehicle))
                return false;
            bool isExecutingCharge = vehicle.CurrentRunningTask().OrderData.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Charge;
            if (isExecutingCharge)
            {

            }
            return isExecutingCharge;
        }

        private bool IsVehicleNoOrder(IAGV vehicle)
        {
            return vehicle.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.NO_ORDER;
        }

    }
}

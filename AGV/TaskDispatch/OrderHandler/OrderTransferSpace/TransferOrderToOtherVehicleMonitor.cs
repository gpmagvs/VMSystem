using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;
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
        private static SemaphoreSlim betterVehicleFindSemaphose = new SemaphoreSlim(1, 1);

        public TransferOrderToOtherVehicleMonitor(IAGV orderOwner, clsTaskDto order, OrderTransferConfiguration configuration) : base(orderOwner, order, configuration)
        {
            if (order.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Carry)
                TargetWorkStationMapPoint = StaMap.GetPointByTagNumber(order.From_Station_Tag);
            else
                TargetWorkStationMapPoint = StaMap.GetPointByTagNumber(order.To_Station_Tag);
        }

        public override async Task<(bool found, IAGV? betterVehicle)> TryFindBetterVehicle()
        {
            IAGV? betterVehicle = null;
            try
            {
                await betterVehicleFindSemaphose.WaitAsync();
                //評估是否有其他車輛當前位置
                double distanceToWorkStationOfOwner = GetTravelDistanceToTargetWorkStation(orderOwner);

                var moreNearToGoalVehicles = OtherVehicles.ToDictionary(vehicle => vehicle, vehicle => GetTravelDistanceToTargetWorkStation(vehicle))
                             .OrderBy(kp => kp.Value)
                             .Where(kp => kp.Value < distanceToWorkStationOfOwner)
                             .ToDictionary(kp => kp.Key, kp => kp.Value);
                //過濾出車上無貨且正在IDLE 或 正在執行充電任務訂單的車輛
                List<KeyValuePair<IAGV, double>> idleOrChargingVehicles = moreNearToGoalVehicles.Where(kp => kp.Key.main_state != clsEnums.MAIN_STATUS.DOWN) //不是當機的車輛
                                                                                                .Where(kp => !kp.Key.IsAGVHasCargoOrHasCargoID()) //車上無貨的車輛
                                                                                                .Where(kp => kp.Key.online_state == clsEnums.ONLINE_STATE.ONLINE) //上線中車輛
                                                                                                .Where(kp => kp.Key.batteryStatus > IAGV.BATTERY_STATUS.LOW) //確認電池狀態
                                                                                                .Where(kp => IsVehicleNoOrder(kp.Key) || IsVehicleExecutingChargeTask(kp.Key) || IsVehicleLoading(kp.Key)) //執行充電任務中 or 空閒中車輛 or 在執行放貨任務的車
                                                                                                .ToList();
                if (idleOrChargingVehicles.Any())
                    betterVehicle = idleOrChargingVehicles.First().Key;

                if (betterVehicle != null)
                {
                    string _distanceEvealuation = string.Join(",", idleOrChargingVehicles.OrderBy(kp => kp.Key.Name).Select(kp => $"{kp.Key.Name}-{kp.Value}m").ToList());
                    Log($"Better Vehicle Found=>{betterVehicle.Name}.Distances to target workstation of origin order owner:{distanceToWorkStationOfOwner}m|| Distances to target workstation of candicators: {_distanceEvealuation}");
                }

                return (betterVehicle != null, betterVehicle);
            }
            finally
            {
                betterVehicleFindSemaphose.Release();
            }
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

        private bool IsVehicleLoading(IAGV vehicle)
        {
            if (vehicle.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                return false;
            TaskBase? _currentTask = vehicle.CurrentRunningTask();
            if (_currentTask == null)
                return false;
            return _currentTask.Stage == VehicleMovementStage.WorkingAtDestination;
        }


        private double GetTravelDistanceToTargetWorkStation(IAGV vehicle)
        {
            PathFinder _pathFinder = new PathFinder();
            AGVSystemCommonNet6.MAP.PathFinder.clsPathInfo _pathInfo = _pathFinder.FindShortestPath(vehicle.currentMapPoint.TagNumber, TargetWorkStationMapPoint.TagNumber, new PathFinder.PathFinderOption
            {
                Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
                Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
                OnlyNormalPoint = false,
            });
            if (_pathInfo == null || _pathInfo.stations.Count == 0)
                return double.MaxValue;
            return _pathInfo.total_travel_distance;
        }

    }
}

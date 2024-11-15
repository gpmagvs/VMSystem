using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;
using VMSystem.TrafficControl;
using VMSystem.AGV.TaskDispatch.Tasks;
using AGVSystemCommonNet6;

namespace VMSystem.AGV
{
    public static class VehicleExtension
    {
        /// <summary>
        /// 取得被設定為不可停車的Tag Numbers
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static IEnumerable<int> GetCanNotReachTags(this IAGV vehicle)
        {
            return vehicle.model == AGVSystemCommonNet6.clsEnums.AGV_TYPE.SUBMERGED_SHIELD ?
                                                 StaMap.Map.TagForbiddenForSubMarineAGV : StaMap.Map.TagForbiddenForForkAGV;
        }

        /// <summary>
        /// 取得被設定為不可停車的地圖點位
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static List<MapPoint> GetCanNotReachMapPoints(this IAGV vehicle)
        {
            return vehicle.GetCanNotReachTags().Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
        }

        /// <summary>
        /// 車輛是否在執行搬運任務且狀態為駛向來源設備
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static bool IsTravalingToSourceWhenExecutingTransferOrder(this IAGV vehicle)
        {
            if (vehicle.IsExecutingOrder(out TaskBase currentRunningTask))
                return currentRunningTask.OrderData.Action == ACTION_TYPE.Carry && (currentRunningTask.Stage == VehicleMovementStage.Traveling_To_Source);
            else
                return false;
        }
        /// <summary>
        /// 車輛是否在執行搬運任務且狀態為駛向終點
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static bool IsTravalingToDestineWhenExecutingTransferOrder(this IAGV vehicle)
        {
            if (vehicle.IsExecutingOrder(out TaskBase currentRunningTask))
                return currentRunningTask.OrderData.Action == ACTION_TYPE.Carry && (currentRunningTask.Stage == VehicleMovementStage.Traveling_To_Destine);
            else
                return false;
        }
        /// <summary>
        /// 車輛是否在執行搬運任務且狀態為在[起點]設備工作中(取放貨)
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static bool IsWorkingAtSourceEQWhenExecutingTransferOrder(this IAGV vehicle)
        {
            if (vehicle.IsExecutingOrder(out TaskBase currentRunningTask))
                return currentRunningTask.OrderData.Action == ACTION_TYPE.Carry && (currentRunningTask.Stage == VehicleMovementStage.WorkingAtSource);
            else
                return false;
        }
        /// <summary>
        /// 車輛是否在執行搬運任務且狀態為在[終點]設備工作中(取放貨)
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static bool IsWorkingAtDestineEQWhenExecutingTransferOrder(this IAGV vehicle)
        {
            if (vehicle.IsExecutingOrder(out TaskBase currentRunningTask))
                return currentRunningTask.OrderData.Action == ACTION_TYPE.Carry && (currentRunningTask.Stage == VehicleMovementStage.WorkingAtDestination);
            else
                return false;
        }


        /// <summary>
        /// 車輛是否在執行搬運任務且狀態為在[終點]設備工作中(取放貨)
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static bool IsLeavingFromChargeStationWhenExecutingTransferOrder(this IAGV vehicle)
        {
            if (vehicle.IsExecutingOrder(out TaskBase currentRunningTask))
                return currentRunningTask.OrderData.Action == ACTION_TYPE.Carry && (currentRunningTask.Stage == VehicleMovementStage.LeaveFrom_ChargeStation);
            else
                return false;
        }

        /// <summary>
        /// 取得距離當前子任務終點的距離
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static double GetDistanceToDestine(this IAGV vehicle)
        {
            if (!vehicle.IsExecutingOrder(out TaskBase currentTask))
                return -1;

            MapPoint destineMapPoint = StaMap.GetPointByTagNumber(currentTask.DestineTag);
            if (destineMapPoint == null)
                return -1;

            return destineMapPoint.CalculateDistance(vehicle.states.Coordination);
        }

        public static bool IsExecutingOrder(this IAGV vehicle, out TaskBase currentTask)
        {
            currentTask = null;
            if (vehicle == null)
                return false;
            currentTask = vehicle.CurrentRunningTask();
            if (currentTask == null)
                return false;
            return vehicle.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
        }


        public static bool TryGetNextOrder(this IAGV agv, string currentTaskID, out clsTaskDto nextOrder)
        {
            nextOrder = null;
            if (agv == null)
                return false;
            nextOrder = agv.taskDispatchModule.taskList.OrderBy(order => order.RecieveTime)
                                                        .FirstOrDefault(order => order.TaskName != currentTaskID && order.State == TASK_RUN_STATUS.WAIT);
            return nextOrder != null;
        }

    }
}

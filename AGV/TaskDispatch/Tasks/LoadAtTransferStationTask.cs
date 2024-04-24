using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class LoadAtTransferStationTask : LoadAtDestineTask
    {
        public LoadAtTransferStationTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
            DestineTag = order.TransferToTag;
        }
        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.LoadingAtTransferStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Load;

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }
        protected override int GetSlotHeight()
        {
            return 0;
        }

        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(OrderData.need_change_agv ? OrderData.TransferToTag : OrderData.To_Station_Tag, ACTION_TYPE.Load);
            return await base.DistpatchToAGV();
        }
        protected override int GetDestineWorkStationTagByOrderInfo(clsTaskDto orderInfo)
        {
            return orderInfo.TransferToTag;
        }
    }
}

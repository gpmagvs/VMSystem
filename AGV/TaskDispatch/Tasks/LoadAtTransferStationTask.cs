using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class LoadAtTransferStationTask : LoadAtDestineTask
    {
        public Dictionary<int, List<int>> dict_Transfer_to_from_tags = new Dictionary<int, List<int>>();

        public LoadAtTransferStationTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
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

        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            DestineTag = OrderData.need_change_agv ? OrderData.TransferToTag : OrderData.To_Station_Tag;
            if (!OrderData.bypass_eq_status_check)
            {
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode, response.message);
            }
            return await base.DistpatchToAGV();
        }
        protected override int GetDestineWorkStationTagByOrderInfo(clsTaskDto orderInfo)
        {
            return orderInfo.TransferToTag;
        }

        public override (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) ActionFinishInvoke()
        {
            ReportLoadCargoToPortDone();
            return base.ActionFinishInvoke();
        }
    }
}

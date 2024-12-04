using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using VMSystem.Extensions;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 在起點設備取貨任務
    /// </summary>
    public class UnloadAtSourceTask : LoadUnloadTask
    {
        public UnloadAtSourceTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.WorkingAtSource;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Unload;

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }

        protected override int GetSlotHeight()
        {
            if (int.TryParse(OrderData.From_Slot, out var height))
                return height;
            else
                return 0;
        }

        protected override void UpdateActionDisplay()
        {
            //終點站放貨
            var equipment = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
            TrafficWaitingState.SetDisplayMessage($"{equipment.Graph.Display}-取貨");
        }

        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                {
                    await HandleAGVSRejectLDULDActionStartReport(response.AlarmCode, response.message);
                    return (response.confirm, response.AlarmCode, response.message);
                }
            }
            _ = MCSCIMService.VehicleArrivedReport(Agv.AgvIDStr, OrderData.soucePortID).ContinueWith(async t =>
            {
                await Task.Delay(100);
                await MCSCIMService.VehicleAcquireStartedReport(this.Agv.AgvIDStr, OrderData.Carrier_ID, OrderData.soucePortID);
            });
            var result = await base.DistpatchToAGV();
            if (this.Agv.IsAGVHasCargoOrHasCargoID() == true)
                OrderData.Actual_Carrier_ID = this.Agv.states.CSTID[0];

            if (result.confirmed)
            {//載具在車上
                orderHandler.transportCommand.CarrierLoc = Agv.AgvIDStr;
                orderHandler.transportCommand.CarrierZoneName = "";
            }

            return result;
        }

        public override async Task<(bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg)> ActionFinishInvoke()
        {
            var baseResult = await base.ActionFinishInvoke();
            MCSCIMService.VehicleAcquireCompletedReport(this.Agv.AgvIDStr, OrderData.Carrier_ID, OrderData.soucePortID);
            await ReportUnloadCargoFromPortDone();
            return baseResult;
        }
    }
}

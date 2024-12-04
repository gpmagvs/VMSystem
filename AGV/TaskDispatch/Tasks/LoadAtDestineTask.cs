using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 到目的地放貨
    /// </summary>
    public class LoadAtDestineTask : LoadUnloadTask
    {
        public LoadAtDestineTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.WorkingAtDestination;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Load;

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }

        protected override int GetSlotHeight()
        {
            if (int.TryParse(OrderData.To_Slot, out var height))
                return height;
            else
                return 0;
        }

        protected override void UpdateActionDisplay()
        {
            //終點站放貨
            var equipment = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
            TrafficWaitingState.SetDisplayMessage($"{equipment.Graph.Display}-放貨");
        }

        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                if (this.Agv.IsAGVHasCargoOrHasCargoID() == true)
                    OrderData.Actual_Carrier_ID = this.Agv.states.CSTID[0];
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                {
                    await HandleAGVSRejectLDULDActionStartReport(response.AlarmCode, response.message);
                    return (response.confirm, response.AlarmCode, response.message);
                }
            }
            MCSCIMService.VehicleDepositStartedReport(this.Agv.AgvIDStr, OrderData.Carrier_ID, OrderData.destinePortID);
            var result = await base.DistpatchToAGV();
            if (result.confirmed)
            {
                //載具在來源
                orderHandler.transportCommand.CarrierLoc = OrderData.destinePortID;
                orderHandler.transportCommand.CarrierZoneName = OrderData.destineZoneID;
            }
            //CarrierTransferFromAGVToPortReport(OrderData.destinePortID, OrderData.destineZoneID);
            await MCSCIMService.VehicleDepositCompletedReport(this.Agv.AgvIDStr, OrderData.Carrier_ID, OrderData.destinePortID);
            return result;
        }

        public override async Task<(bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg)> ActionFinishInvoke()
        {
            await ReportLoadCargoToPortDone();
            return await base.ActionFinishInvoke();
        }
    }



}

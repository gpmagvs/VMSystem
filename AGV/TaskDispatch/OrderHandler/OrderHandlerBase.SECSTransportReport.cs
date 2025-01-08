using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Microservices.MCS;
using static AGVSystemCommonNet6.Microservices.MCS.MCSCIMService;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public partial class OrderHandlerBase
    {

        /// <summary>
        /// This is used to store the transport command data for SECS report.
        /// </summary>
        public TransportCommandDto transportCommand = new TransportCommandDto();

        public bool isTransferCanceledByHost = false;
        public bool isTransferAbortedByHost = false;

        public async Task SECS_TranferInitiatedReport()
        {
            if (OrderData.isVehicleAssignedChanged)
                return;
            await MCSCIMService.TransferInitiatedReport(transportCommand);
        }

        public async Task SECS_TransferCompletedReport(AGVSystemCommonNet6.Alarm.ALARMS alarm)
        {
            try
            {
                AGVSystemCommonNet6.Microservices.MCSCIM.TransferReportConfiguration.clsResultCodes transferResultCodes = secsConfigsService.transferReportConfiguration.ResultCodes;
                if (OrderData.isVehicleAssignedChanged)
                    return;

                bool isCargoOnVehicle = Agv.IsAGVHasCargoOrHasCargoID();
                bool isIDReadFail = alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_READ_FAIL;
                bool isIDReadMissmatch = alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED;
                if (Agv.IsAGVHasCargoOrHasCargoID())
                {
                    transportCommand.CarrierLoc = Agv.options.AGV_ID;
                    transportCommand.CarrierZoneName = "";
                }
                else
                {
                    if (RunningTask.Stage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.WorkingAtDestination)
                    {
                        transportCommand.CarrierLoc = OrderData.destinePortID;
                        transportCommand.CarrierZoneName = OrderData.destineZoneID;
                    }
                    else if (RunningTask.Stage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.WorkingAtSource)
                    {
                        transportCommand.CarrierLoc = OrderData.soucePortID;
                        transportCommand.CarrierZoneName = OrderData.sourceZoneID;
                    }
                }

                if (alarm == ALARMS.NONE && Agv.main_state != clsEnums.MAIN_STATUS.DOWN)
                    transportCommand.ResultCode = 0;
                else
                {
                    if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN || alarm == ALARMS.AGV_STATUS_DOWN)
                    {
                        if (RunningTask.ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Load) // 放貨
                            transportCommand.ResultCode = Agv.IsAGVHasCargoOrHasCargoID() ? transferResultCodes.AGVDownWhenLDULDWithCargoResultCode : transferResultCodes.AGVDownWhenLDWithoutCargoResultCode; //AGV車上還有貨:表示放貨前就異常 反之在放好貨後(退出設備)時異常
                        else if (RunningTask.ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Unload) //取貨
                            transportCommand.ResultCode = Agv.IsAGVHasCargoOrHasCargoID() ? transferResultCodes.AGVDownWhenLDULDWithCargoResultCode : transferResultCodes.AGVDownWhenULDWithoutCargoResultCode; //AGV車上有貨:表示取貨後異常 反之在侵入設備時異常
                        else
                            transportCommand.ResultCode = transferResultCodes.AGVDownWhenMovingToDestineResultCode;
                    }
                    else
                    {
                        transportCommand.ResultCode = secsConfigsService.transferReportConfiguration.GetResultCode(alarm);
                    }
                    await Task.Delay(100);
                }

                if (isIDReadMissmatch || isIDReadFail)
                {
                    bool isTrayTypeCargo = OrderData.Carrier_ID.StartsWith("T");
                    string newInstallID = "";
                    if (isIDReadFail)
                        newInstallID = isTrayTypeCargo ? await AGVSConfigulator.GetTrayUnknownFlowID() : await AGVSConfigulator.GetRackUnknownFlowID();
                    else // misbatch
                    {
                        newInstallID = Agv.states.CSTID.FirstOrDefault();
                        await MCSCIMService.CarrierIDReadReport(OrderData.Carrier_ID, Agv.AgvIDStr, ID_READ_STATE.Mismatch);
                        await Task.Delay(200);
                    }
                    transportCommand.CarrierID = newInstallID;
                    await MCSCIMService.TransferCompletedReport(transportCommand);
                    await Task.Delay(200);
                    await MCSCIMService.CarrierRemoveCompletedReport(OrderData.Carrier_ID, Agv.AgvIDStr, "", 1);
                    await Task.Delay(200);
                    await MCSCIMService.CarrierInstallCompletedReport(newInstallID, Agv.AgvIDStr, "", 1);
                }
                else
                    await MCSCIMService.TransferCompletedReport(transportCommand);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }
    }
}

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
                if (OrderData.isVehicleAssignedChanged)
                    return;

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

                if (alarm == ALARMS.NONE)
                    transportCommand.ResultCode = 0;
                else if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                {

                    if (RunningTask.ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Load) // 放貨
                        transportCommand.ResultCode = Agv.IsAGVHasCargoOrHasCargoID() ? 101 : 102; //AGV車上還有貨:表示放貨前就異常 反之在放好貨後(退出設備)時異常
                    else if (RunningTask.ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Unload) //取貨
                        transportCommand.ResultCode = Agv.IsAGVHasCargoOrHasCargoID() ? 101 : 144; //AGV車上有貨:表示取貨後異常 反之在侵入設備時異常
                    else
                        transportCommand.ResultCode = 145;
                }
                else
                {
                    if (alarm == ALARMS.UNLOAD_BUT_CARGO_ID_READ_FAIL)
                    {
                        transportCommand.ResultCode = 5;
                    }
                    else if (alarm == ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED)
                        transportCommand.ResultCode = 4;
                    else if (alarm == ALARMS.EQ_UNLOAD_REQUEST_ON_BUT_NO_CARGO)
                        transportCommand.ResultCode = 100;
                    else
                        transportCommand.ResultCode = (int)alarm;
                }
                var transferComptReportCmd = transportCommand.Clone();
                bool isCargoOnVehicle = Agv.IsAGVHasCargoOrHasCargoID();
                bool isIDReadFail = alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_READ_FAIL;
                bool isIDReadMissmatch = alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED;
                if (isIDReadMissmatch || isIDReadFail)
                {
                    bool isTrayTypeCargo = OrderData.Carrier_ID.StartsWith("T");
                    if (isIDReadFail)
                    {
                        transferComptReportCmd.CarrierID = isTrayTypeCargo ? await AGVSConfigulator.GetTrayUnknownFlowID() : await AGVSConfigulator.GetRackUnknownFlowID();
                        MCSCIMService.CarrierRemoveCompletedReport(OrderData.Carrier_ID, Agv.AgvIDStr, "", 1).ContinueWith(async t =>
                        {
                            await MCSCIMService.CarrierInstallCompletedReport(transferComptReportCmd.CarrierID, Agv.AgvIDStr, "", 1);
                        });
                    }
                    else // misbatch
                    {
                        transferComptReportCmd.CarrierID = isTrayTypeCargo ? await AGVSConfigulator.GetTrayMissMatchFlowID() : await AGVSConfigulator.GetRackMissMatchFlowID();
                        MCSCIMService.CarrierRemoveCompletedReport(OrderData.Carrier_ID, Agv.AgvIDStr, "", 1).ContinueWith(async t =>
                        {
                            await MCSCIMService.CarrierInstallCompletedReport(transferComptReportCmd.CarrierID, Agv.AgvIDStr, "", 1);
                        });
                    }
                }
                if (alarm == AGVSystemCommonNet6.Alarm.ALARMS.EQ_UNLOAD_REQUEST_ON_BUT_NO_CARGO) //來源空值
                {
                    transferComptReportCmd.CarrierID = await AGVSConfigulator.GetTrayUnknownFlowID();
                }
                await MCSCIMService.TransferCompletedReport(transferComptReportCmd);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }
    }
}

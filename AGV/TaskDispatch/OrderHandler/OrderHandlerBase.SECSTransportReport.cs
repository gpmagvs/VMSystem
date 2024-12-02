using AGVSystemCommonNet6;
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
            await MCSCIMService.TransferInitiatedReport(transportCommand);
        }

        public async Task SECS_TransferringReport()
        {
            await MCSCIMService.TransferringReport(transportCommand);
        }
        public async Task SECS_TransferCompletedReport(AGVSystemCommonNet6.Alarm.ALARMS alarm = AGVSystemCommonNet6.Alarm.ALARMS.NONE)
        {
            try
            {
                var transferComptReportCmd = transportCommand.Clone();
                transferComptReportCmd.CarrierLoc = OrderData.destinePortID;
                transferComptReportCmd.CarrierZoneName = OrderData.destineZoneID;
                bool isIDReadFail = alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_READ_FAIL;

                if (alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED || isIDReadFail)
                {
                    if (isIDReadFail)
                        transferComptReportCmd.CarrierID = await AGVSConfigulator.GetTrayUnknownFlowID();
                    else
                        transferComptReportCmd.CarrierID = Agv.states.CSTID.FirstOrDefault();
                    transferComptReportCmd.CarrierLoc = Agv.AgvIDStr;
                }
                if (alarm == AGVSystemCommonNet6.Alarm.ALARMS.EQ_UNLOAD_REQUEST_ON_BUT_NO_CARGO) //來源空值
                {
                    transferComptReportCmd.CarrierID =await AGVSConfigulator.GetTrayUnknownFlowID();
                    transferComptReportCmd.CarrierLoc = OrderData.soucePortID;
                    transferComptReportCmd.CarrierZoneName = OrderData.sourceZoneID;
                }

                if (alarm == AGVSystemCommonNet6.Alarm.ALARMS.EQ_LOAD_REQUEST_ON_BUT_HAS_CARGO) //目的地有貨
                {
                    //transferComptReportCmd.CarrierID =await AGVSConfigulator.GetTrayUnknownFlowID();
                    if (this.RunningTask.Stage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.Traveling_To_Destine ||
                        this.RunningTask.Stage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.WorkingAtDestination)
                    {
                        transferComptReportCmd.CarrierLoc = Agv.AgvIDStr;
                        transferComptReportCmd.CarrierZoneName = "";
                    }
                    transferComptReportCmd.ResultCode = 1;
                }

                await MCSCIMService.TransferCompletedReport(transferComptReportCmd);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }
        public async Task SECS_TransferCompletedReport(int resultCode)
        {
            transportCommand.ResultCode = resultCode;
            await SECS_TransferCompletedReport();
        }
        public async Task SECS_TransferAbortInitiatedReport()
        {
        }
        public async Task SECS_TransferCancelInitiatedReport()
        {
        }
        public async Task SECS_TransferAbortFailedReport()
        {
        }
        public async Task SECS_TransferCancelFailedReport()
        {

        }
    }
}

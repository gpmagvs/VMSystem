using AGVSystemCommonNet6;
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
        public async Task SECS_TranferInitiatedReport()
        {
            await MCSCIMService.TransferInitiatedReport(transportCommand);
        }

        public async Task SECS_TransferringReport()
        {
            await MCSCIMService.TransferringReport(transportCommand);
        }
        public async Task SECS_TransferCompletedReport()
        {
            try
            {
                var transferComptReportCmd = transportCommand.Clone();
                transferComptReportCmd.CarrierLoc = OrderData.destinePortID;
                transferComptReportCmd.CarrierZoneName = OrderData.destineZoneID;
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

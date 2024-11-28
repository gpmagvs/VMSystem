﻿using AGVSystemCommonNet6.Microservices.MCS;
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
        }

        public async Task SECS_TransferringReport()
        {
        }
        public async Task SECS_TransferCompletedReport()
        {
            try
            {
                await MCSCIMService.TransferCompletedReport(transportCommand);
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

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
                if (alarm == ALARMS.NONE)
                    transportCommand.ResultCode = 0;
                else
                {
                    if (alarm == ALARMS.UNLOAD_BUT_CARGO_ID_READ_FAIL)
                        transportCommand.ResultCode = 5;
                    else if (alarm == ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED)
                        transportCommand.ResultCode = 4;
                    else if (alarm == ALARMS.EQ_UNLOAD_REQUEST_ON_BUT_NO_CARGO)
                        transportCommand.ResultCode = 100;
                    else
                        transportCommand.ResultCode = 1;
                }
                var transferComptReportCmd = transportCommand.Clone();
                bool isIDReadFail = alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_READ_FAIL;

                bool isCargoOnVehicle = Agv.IsAGVHasCargoOrHasCargoID();

                if (alarm == AGVSystemCommonNet6.Alarm.ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED || isIDReadFail)
                {
                    if (isIDReadFail)
                        transferComptReportCmd.CarrierID = await AGVSConfigulator.GetTrayUnknownFlowID();
                    else
                        transferComptReportCmd.CarrierID = Agv.states.CSTID.FirstOrDefault();
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

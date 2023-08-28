using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using static AGVSystemCommonNet6.AGVDispatch.clsAGVSTcpServer;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.AGV;

namespace VMSystem.VMS
{
    public partial class VMSManager
    {
        public static clsAGVSTcpServer TcpServer = new clsAGVSTcpServer();

        private static void TcpServer_OnClientConnected(object? sender, clsAGVSTcpClientHandler clientState)
        {
            clientState.OnClientOnlineModeQuery += ClientState_OnTCPClientOnlineModeQuery;
            clientState.OnClientRunningStatusReport += ClientState_OnTCPClientRunningStatusReport;
            clientState.OnClientTaskFeedback += ClientState_OnClientTaskFeedback;
        }


        private static void ClientState_OnTCPClientRunningStatusReport(object? sender, clsRunningStatusReportMessage e)
        {
            Task.Factory.StartNew(() =>
            {
                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(e.EQName, AGV_MODEL.FORK_AGV, out IAGV agv))
                {
                    agv.states = (e.Header.Values.First());
                    client.SendJsonReply(AGVSMessageFactory.CreateSimpleReturnMessageData(e, "0106", RETURN_CODE.OK));
                }
            });
        }

        private static void ClientState_OnTCPClientOnlineModeQuery(object? sender, clsOnlineModeQueryMessage e)
        {
            Task.Factory.StartNew(() =>
            {
                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(e.EQName, AGV_MODEL.FORK_AGV, out IAGV agv))
                {
                    agv.connected = true;
                }
                client.SendJsonReply(AGVSMessageFactory.createOnlineModeAckData(e, REMOTE_MODE.OFFLINE));
            });
        }

        private static void ClientState_OnClientTaskFeedback(object? sender, clsTaskFeedbackMessage e)
        {
            Task.Factory.StartNew(() =>
            {
                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(e.EQName, AGV_MODEL.FORK_AGV, out IAGV agv))
                {
                    agv.taskDispatchModule.TaskFeedback(e.Header.Values.First());
                    client.SendJsonReply(AGVSMessageFactory.CreateSimpleReturnMessageData(e, "0304", RETURN_CODE.OK));
                }
            });
        }
        private static void ClientState_OnClientMsgSendIn(object? sender, clsAGVSTcpServer.clsAGVSTcpClientHandler.clsMsgSendEventArg MsgSendDto)
        {
        }

    }
}

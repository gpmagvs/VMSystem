using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using System.Diagnostics;
using VMSystem.AGV;
using static AGVSystemCommonNet6.AGVDispatch.clsAGVSTcpServer;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{
    public partial class VMSManager
    {
        public static clsAGVSTcpServer TcpServer = new clsAGVSTcpServer();
        public struct Tests
        {
            public static bool AGVRunningStatusReportT1TimeoutSimulationFlag = false;
            public static bool AGVTaskFeedfackReportT1TimeoutSimulationFlag = false;
            public static bool AGVOnlineModeQueryT1TimeoutSimulationFlag = false;
        }
        private static void TcpServer_OnClientConnected(object? sender, clsAGVSTcpClientHandler clientState)
        {
            clientState.OnClientOnlineModeQuery += ClientState_OnTCPClientOnlineModeQuery;
            clientState.OnClientOnlineRequesting += ClientState_OnClientOnlineRequesting;
            clientState.OnClientRunningStatusReport += ClientState_OnTCPClientRunningStatusReport;
            clientState.OnClientTaskFeedback += ClientState_OnClientTaskFeedback;

            clientState.OnTcpSocketDisconnect += ClientState_OnTcpSocketDisconnect;
        }

        private static void ClientState_OnTcpSocketDisconnect(object? sender, EventArgs e)
        {
            Task.Factory.StartNew(() =>
            {
                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(client.AGV_Name, clsEnums.AGV_TYPE.FORK, out IAGV agv))
                {
                    agv.online_mode_req = ONLINE_STATE.OFFLINE;
                    agv.online_state = ONLINE_STATE.OFFLINE;
                }
            });
        }

        private static void ClientState_OnTCPClientRunningStatusReport(object? sender, clsRunningStatusReportMessage e)
        {
            Task.Factory.StartNew(() =>
            {
                if (Tests.AGVRunningStatusReportT1TimeoutSimulationFlag)
                    return;

                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(e.EQName, clsEnums.AGV_TYPE.FORK, out IAGV agv))
                {
                    agv.states = (e.Header.Values.First()).ToWebAPIRunningStatusObject();
                    client.SendJsonReply(AGVSMessageFactory.CreateSimpleReturnMessageData(e, "0106", RETURN_CODE.OK));
                }
            });
        }

        private static void ClientState_OnTCPClientOnlineModeQuery(object? sender, clsOnlineModeQueryMessage e)
        {
            Task.Factory.StartNew(() =>
            {
                if (Tests.AGVOnlineModeQueryT1TimeoutSimulationFlag)
                    return;

                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(e.EQName, clsEnums.AGV_TYPE.FORK, out IAGV agv))
                {
                    agv.connected = true;
                    agv.TcpClientHandler = client;
                    client.SendJsonReply(AGVSMessageFactory.createOnlineModeAckData(e, agv.online_mode_req == ONLINE_STATE.ONLINE ? REMOTE_MODE.ONLINE : REMOTE_MODE.OFFLINE));
                }
            });
        }

        /// <summary>
        /// AGV要求上下線
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request_message"></param>
        /// <exception cref="NotImplementedException"></exception>
        private static void ClientState_OnClientOnlineRequesting(object? sender, clsOnlineModeRequestMessage request_message)
        {
            Task.Factory.StartNew(() =>
            {
                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(request_message.EQName, clsEnums.AGV_TYPE.FORK, out IAGV agv))
                {
                    var remote_req = request_message.Header.Values.First().ModeRequest;
                    if (remote_req == REMOTE_MODE.ONLINE)
                        agv.AGVOnlineFromAGV(out string msg);
                    else
                        agv.AGVOfflineFromAGV(out string msg);

                    client.SendJsonReply(AGVSMessageFactory.CreateSimpleReturnMessageData(request_message, "0104", RETURN_CODE.OK));
                }
                else
                {
                    client.SendJsonReply(AGVSMessageFactory.CreateSimpleReturnMessageData(request_message, "0104", RETURN_CODE.NG));
                }
            });
        }
        private static void ClientState_OnClientTaskFeedback(object? sender, clsTaskFeedbackMessage e)
        {
            Task.Factory.StartNew(() =>
            {
                if (Tests.AGVTaskFeedfackReportT1TimeoutSimulationFlag)
                    return;

                clsAGVSTcpClientHandler client = (clsAGVSTcpClientHandler)sender;
                if (TryGetAGV(e.EQName, clsEnums.AGV_TYPE.FORK, out IAGV agv))
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

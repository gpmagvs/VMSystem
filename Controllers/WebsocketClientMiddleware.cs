using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    public class WebsocketClientMiddleware
    {
        public enum WS_DATA_TYPE
        {
            DynamicTrafficData,
            AGVNaviPathsInfo,
            VMSAliveCheck,
            VMSStatus
        }

        public static async Task ClientRequest(HttpContext _HttpContext, WS_DATA_TYPE client_req)
        {
            if (_HttpContext.WebSockets.IsWebSocketRequest)
            {
                WebSocket webSocket = await _HttpContext.WebSockets.AcceptWebSocketAsync();
                MessageSender msg_sender = new MessageSender(webSocket, client_req);
                msg_sender.OnViewDataFetching += () => { return GetData(client_req); };
                await msg_sender.SendMessage();
                msg_sender.Dispose();
            }
            else
            {
                _HttpContext.Response.StatusCode = 400;
            }
        }
        public class MessageSender : IDisposable
        {
            public WebSocket client { get; private set; }
            public WS_DATA_TYPE client_req { get; }

            internal delegate object OnViewDataFetchDelate();
            internal OnViewDataFetchDelate OnViewDataFetching;
            private bool disposedValue;

            public MessageSender(WebSocket client, WS_DATA_TYPE client_req)
            {
                this.client = client;
                this.client_req = client_req;
            }

            public async Task SendMessage()
            {
                var buff = new ArraySegment<byte>(new byte[10]);
                bool closeFlag = false;
                _ = Task.Factory.StartNew(async () =>
                {
                    while (!closeFlag)
                    {
                        await Task.Delay(100);

                        if (OnViewDataFetching == null)
                            return;
                        var data = OnViewDataFetching();
                        if (data != null)
                        {
                            try
                            {

                                await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data))), WebSocketMessageType.Text, true, CancellationToken.None);
                                data = null;
                            }
                            catch (Exception)
                            {
                                return;
                            }
                        }
                    }
                });

                while (true)
                {
                    try
                    {
                        await Task.Delay(100);
                        WebSocketReceiveResult result = await client.ReceiveAsync(buff, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        break;
                    }
                }
                closeFlag = true;
                client.Dispose();
                GC.Collect();

            }

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        // TODO: 處置受控狀態 (受控物件)
                    }

                    // TODO: 釋出非受控資源 (非受控物件) 並覆寫完成項
                    // TODO: 將大型欄位設為 Null
                    OnViewDataFetching = null;
                    client = null;
                    disposedValue = true;
                }
            }
            public void Dispose()
            {
                // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }



        private static object DynamicTrafficData;
        private static object AGVNaviPathsInfo;
        private static object VMSAliveCheck;
        private static object VMSStatus;

        internal static async Task StartViewDataCollect()
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    Thread.Sleep(10);
                    DynamicTrafficData = ViewModelFactory.GetDynamicTrafficDataVM();
                    AGVNaviPathsInfo = ViewModelFactory.GetAGVNaviPathsInfoVM();
                    VMSAliveCheck = ViewModelFactory.GetVMSAliveCheckVM();
                    VMSStatus = ViewModelFactory.GetVMSStatusData();
                }
            });
        }


        private static object GetData(WS_DATA_TYPE client_req)
        {
            object viewmodel = "";
            switch (client_req)
            {
                case WS_DATA_TYPE.DynamicTrafficData:
                    viewmodel = DynamicTrafficData;
                    break;
                case WS_DATA_TYPE.AGVNaviPathsInfo:
                    viewmodel = AGVNaviPathsInfo;
                    break;
                case WS_DATA_TYPE.VMSAliveCheck:
                    viewmodel = VMSAliveCheck;
                    break;
                case WS_DATA_TYPE.VMSStatus:
                    viewmodel = VMSStatus;
                    break;
                default:
                    break;
            }
            return viewmodel;
        }

        private static class ViewModelFactory
        {
            public static object GetDynamicTrafficDataVM()
            {
                return TrafficControlCenter.DynamicTrafficState;
            }
            public static object GetAGVNaviPathsInfoVM()
            {
                object GetNavigationData(AGV.IAGV agv)
                {
                    if (agv.currentMapPoint == null)
                        return new { };
                    var taskRuningStatus = agv.taskDispatchModule.TaskStatusTracker.TaskRunningStatus;
                    return new
                    {
                        currentLocation = agv.currentMapPoint.TagNumber,
                        currentCoordication = agv.states.Coordination,
                        cargo_status = new
                        {
                            exist = agv.states.Cargo_Status == 1,
                            cargo_type = agv.states.CargoType,
                            cst_id = agv.states.CSTID.FirstOrDefault()
                        },
                        nav_path = agv.NavigatingTagPath,
                        theta = agv.states.Coordination.Theta,
                        waiting_info = agv.taskDispatchModule.TaskStatusTracker.waitingInfo,
                        states = new
                        {
                            is_online = agv.online_state == ONLINE_STATE.ONLINE,
                            is_executing_task = taskRuningStatus == TASK_RUN_STATUS.NAVIGATING | taskRuningStatus == TASK_RUN_STATUS.ACTION_START,
                            main_status = agv.main_state
                        },
                        currentAction = agv.taskDispatchModule.TaskStatusTracker.currentActionType
                    };
                }
                return VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => GetNavigationData(agv));
            }
            public static object GetVMSAliveCheckVM()
            {
                return true;
            }
            public static object GetVMSStatusData()
            {
                return VMSManager.GetVMSViewData();
            }
        }

    }
}

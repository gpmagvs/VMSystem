using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.VMS;
using AGVSystemCommonNet6.HttpTools;

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
                clsWebsocktClientHandler clientHander = new clsWebsocktClientHandler(webSocket, client_req.ToString());
                clientHander.OnDataFetching += (path) => { return GetData(path); };
                await clientHander.StartBrocast();
            }
            else
            {
                _HttpContext.Response.StatusCode = 400;
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

        private static object GetData(string client_req)
        {
            object viewmodel = "";
            switch (client_req)
            {
                case "DynamicTrafficData":
                    viewmodel = DynamicTrafficData;
                    break;
                case "AGVNaviPathsInfo":
                    viewmodel = AGVNaviPathsInfo;
                    break;
                case "VMSAliveCheck":
                    viewmodel = VMSAliveCheck;
                    break;
                case "VMSStatus":
                    viewmodel = VMSStatus;
                    break;
                default:
                    break;
            }
            return viewmodel;
        }
        private static object GetData(WS_DATA_TYPE client_req)
        {
            return GetData(client_req.ToString());
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

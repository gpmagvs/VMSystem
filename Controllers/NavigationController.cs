using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.TASK;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NavigationController : ControllerBase
    {

        private object CollectAGVNavigatingPath()
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
                    states = new {
                        is_online =agv.online_state== ONLINE_STATE.ONLINE,
                        is_executing_task= taskRuningStatus == TASK_RUN_STATUS.NAVIGATING| taskRuningStatus == TASK_RUN_STATUS.ACTION_START,
                        main_status = agv.main_state
                    },
                    currentAction = agv.taskDispatchModule.TaskStatusTracker.currentActionType
                };
            }
            return VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => GetNavigationData(agv));
        }
        /// <summary>
        /// 收集所有AGV當前的導航路徑
        /// </summary>
        /// <returns></returns>
        [HttpGet("/ws/AGVNaviPathsInfo")]
        public async Task NaviPaths()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var websocket_client = await HttpContext.WebSockets.AcceptWebSocketAsync();

                while (websocket_client.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        await Task.Delay(500);
                        byte[] rev_buffer = new byte[4096];
                        websocket_client.ReceiveAsync(new ArraySegment<byte>(rev_buffer), CancellationToken.None);

                        var data = CollectAGVNavigatingPath();

                        await websocket_client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data))), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

                    }
                    catch (Exception ex)
                    {
                        return;
                    }
                }
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NavigationController : ControllerBase
    {

        private Dictionary<string, List<int>> CollectAGVNavigatingPath()
        {
            return VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => agv.NavigatingTagPath);
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

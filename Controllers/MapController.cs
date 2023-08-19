using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.TrafficControl;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MapController : ControllerBase
    {
        [HttpGet("Reload")]
        public async Task<IActionResult> Reload(string map_file)
        {
            StaMap.Download();
            return Ok(true);
        }


        [HttpGet("Regist")]
        public async Task Regist(int Tag_Number)
        {
            var map_point = StaMap.GetPointByTagNumber(Tag_Number);
            StaMap.RegistPoint("System", map_point);
        }
        [HttpGet("Unregist")]
        public async Task Unregist(int Tag_Number)
        {
            var map_point = StaMap.GetPointByTagNumber(Tag_Number);
            StaMap.UnRegistPoint("System", map_point);
        }


        [HttpGet("/ws/DynamicTrafficData")]
        public async Task GetDynamicTrafficData()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var websocket_client = await HttpContext.WebSockets.AcceptWebSocketAsync();
                byte[] rev_buffer = new byte[4096];
                while (websocket_client.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        await Task.Delay(100);
                        websocket_client.ReceiveAsync(new ArraySegment<byte>(rev_buffer), CancellationToken.None);
                        await websocket_client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(TrafficControlCenter.DynamicTrafficState))), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.TrafficControl;
using static SQLite.SQLite3;

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
        public async Task<IActionResult> Regist(int Tag_Number)
        {
            var map_point = StaMap.GetPointByTagNumber(Tag_Number);
            bool result = StaMap.RegistPoint("System", map_point, out string err_msg);
            return Ok(new { result = result, message = err_msg });
        }
        [HttpGet("Unregist")]
        public async Task<IActionResult> Unregist(int Tag_Number)
        {
            var map_point = StaMap.GetPointByTagNumber(Tag_Number);
            bool result = StaMap.UnRegistPoint("System", map_point, out string err_msg);
            return Ok(new { result = result, message = err_msg });

        }


        [HttpGet("/ws/DynamicTrafficData")]
        public async Task GetDynamicTrafficData()
        {
            await WebsocketClientMiddleware.ClientRequest(HttpContext, WebsocketClientMiddleware.WS_DATA_TYPE.DynamicTrafficData);
        }
    }
}

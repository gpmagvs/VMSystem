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
            bool result = StaMap.RegistPointBySystem(map_point, out var err_msg);
            return Ok(new { result = result, message = err_msg });
        }
        [HttpGet("Unregist")]
        public async Task<IActionResult> Unregist(int Tag_Number)
        {
            var map_point = StaMap.GetPointByTagNumber(Tag_Number);
            var result = await StaMap.UnRegistPointBySystem(map_point);
            return Ok(new { result = result.success, message = result.error_message });

        }

        [HttpPost("RegistPartsRegion")]
        public async Task<IActionResult> RegistPartsRegion(string regionName, string agvName)
        {
            var result = await TrafficControl.PartsAGVSHelper.RegistStationRequestToAGVS(new List<string>() { regionName }, agvName);
            return Ok(new
            {
                confirm = result.confirm,
                message = result.message
            });
        }
    }
}

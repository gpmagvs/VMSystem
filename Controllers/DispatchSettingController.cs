using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.Dispatch;
using VMSystem.Dispatch.Configurations;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DispatchSettingController : ControllerBase
    {
        clsOptimizeAGVDispatcher _optimizeAGVDispatcher;
        public DispatchSettingController(clsOptimizeAGVDispatcher optimizeAGVDispatcher)
        {
            _optimizeAGVDispatcher = optimizeAGVDispatcher;
        }
        [HttpGet("GetEQStationDedicatedSetting")]
        public async Task<List<EQStationDedicatedSetting>> GetEQStationDedicatedSetting()
        {
            return _optimizeAGVDispatcher.eqStationDedicatedConfig.EQStationDedicatedSettings;
        }

        [HttpPost("SetEQStationDedicatedSetting")]
        public async Task SeEQStationDedicatedSetting([FromBody] List<EQStationDedicatedSetting> EQStationDedicatedSettings)
        {
            _optimizeAGVDispatcher.SeEQStationDedicatedSetting(EQStationDedicatedSettings);
        }
    }
}

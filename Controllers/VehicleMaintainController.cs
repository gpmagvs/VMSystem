using AGVSystemCommonNet6.Maintainance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.Services;
namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VehicleMaintainController : ControllerBase
    {
        private VehicleMaintainService maintainService;
        public VehicleMaintainController(VehicleMaintainService maintainService)
        {
            this.maintainService = maintainService;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            List<VehicleMaintain> maintainsettings = maintainService.GetAllMaintainSettings();
            List<string> agvNames = maintainsettings.Select(v => v.AGV_Name).Distinct().ToList();

            Dictionary<string, List<VehicleMaintain>> agvSettings = agvNames.ToDictionary(agvName => agvName,
                                  agvName => maintainsettings.Where(setting => setting.AGV_Name == agvName)
                                                             .OrderBy(v => v.MaintainItem).ToList());
            return Ok(agvSettings);
        }

        [HttpPost("ResetCurrentValue")]
        public async Task<bool> ResetCurrentValue(string agvName, MAINTAIN_ITEM item)
        {
            return await maintainService.ResetCurrentValue(agvName, item);
        }

        [HttpPost("SettingMaintainValue")]
        public async Task<bool> SettingMaintainValue(string agvName, MAINTAIN_ITEM item, double value)
        {
            return await maintainService.SettingMaintainValue(agvName, item, value);
        }
    }
}

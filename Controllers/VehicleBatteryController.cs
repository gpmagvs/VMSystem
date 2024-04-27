using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.AGV;
using VMSystem.ViewModels;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VehicleBatteryController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            VehicleChargeDataViewModel createViewModel(IAGV vehicle)
            {
                var batOption = vehicle.options.BatteryOptions;
                return new VehicleChargeDataViewModel(vehicle.Name, batOption.LowLevel, batOption.MiddleLevel, batOption.HightLevel);
            }
            var data = VMSManager.AllAGV.Select(vehicle => createViewModel(vehicle)).OrderBy(v => v.agvName).ToArray();
            return Ok(data);
        }

        [HttpPost]
        public async Task<IActionResult> Modify(string agvName, [FromBody] VehicleChargeDataViewModel payload)
        {
            try
            {
                var agv = VMSManager.GetAGVByName(agvName);
                if (agv == null)
                    return BadRequest($"{agvName} not exist in system");
                agv.options.BatteryOptions.HightLevel = payload.highLevel;
                agv.options.BatteryOptions.MiddleLevel = payload.middleLevel;
                agv.options.BatteryOptions.LowLevel = payload.lowLevel;
                return Ok();
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}

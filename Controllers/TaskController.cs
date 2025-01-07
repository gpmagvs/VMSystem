using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion.Internal;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TaskController : ControllerBase
    {
        private AGVSDbContext _dbContent;

        public TaskController(AGVSDbContext dbcontent)
        {
            _dbContent = dbcontent;
        }

        [HttpGet("Cancel")]
        public async Task<IActionResult> Cancel(string task_name, string reason = "", string? hostAction = "cancel")
        {
            try
            {
                await VMSManager.TaskCancel(task_name, reason, hostAction);
                return Ok("done");
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        [HttpGet("CheckOrderExecutableByBatStatus")]
        public async Task<IActionResult> CheckOrderExecutableByBatStatus(string agvName, ACTION_TYPE orderAction)
        {
            var vehicle = VMSManager.GetAGVByName(agvName);
            if (vehicle == null)
                return Ok(new clsResponseBase() { confirm = false, message = $"{agvName} Not Exist." });

            bool accept = vehicle.CheckOutOrderExecutableByBatteryStatusAndChargingStatus(orderAction, out string message);
            return Ok(new clsResponseBase() { confirm = accept, message = message });
        }

        [HttpPost("SettingNoRunRandomCarryHotRunAGVList")]
        public async Task<IActionResult> SettingNoRunRandomCarryHotRunAGVList([FromBody] List<string> agvNameList)
        {
            // VMSManager.OptimizeAGVDisaptchModule.NoAcceptRandomCarryHotRunAGVNameList = agvNameList;
            return Ok(true);
        }

        [HttpGet("NoRunRandomCarryHotRunAGVList")]
        public async Task<IActionResult> GetNoRunRandomCarryHotRunAGVList()
        {
            //return Ok(VMSManager.OptimizeAGVDisaptchModule.NoAcceptRandomCarryHotRunAGVNameList);
            return Ok();
        }
    }
}

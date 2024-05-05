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
        public async Task<IActionResult> Cancel(string task_name)
        {
            try
            {
                VMSManager.TaskCancel(ref _dbContent, task_name);
                //_dbContent.SaveChanges();
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
                return Ok(new clsResponseBase(false, $"{agvName} Not Exist."));

            bool accept = vehicle.CheckOutOrderExecutableByBatteryStatusAndChargingStatus(orderAction, out string message);
            return Ok(new clsResponseBase(accept, message));
        }
    }
}

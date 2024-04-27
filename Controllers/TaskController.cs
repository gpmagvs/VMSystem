using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion.Internal;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TaskController : ControllerBase
    {

        [HttpGet("Cancel")]
        public async Task<IActionResult> Cancel(string task_name)
        {
            var taskOwnerAGV = VMSManager.AllAGV.FirstOrDefault(agv => agv.taskDispatchModule.taskList.Any(tk => tk.TaskName == task_name));

            if (taskOwnerAGV == null)
            {
                return Ok("");
            }
            bool isTaskExecuting = taskOwnerAGV.taskDispatchModule.OrderHandler.OrderData.TaskName == task_name;
            if (isTaskExecuting)
            {
                await taskOwnerAGV.taskDispatchModule.OrderHandler.CancelOrder("User Cancel");
            }
            taskOwnerAGV.taskDispatchModule.AsyncTaskQueueFromDatabase();

            return Ok("done");
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

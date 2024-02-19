using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
            var taskHandler = VMSManager.AllAGV.Select(agv => agv.taskDispatchModule.OrderHandler).FirstOrDefault(handler => handler.OrderData.TaskName == task_name);
            if (taskHandler == null)
                return Ok("Task is not tracking");
            taskHandler.CancelOrder("User Cancel");
            return Ok("done");
        }
    }
}

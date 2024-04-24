using AGVSystemCommonNet6.DATABASE;
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
            else
            {
                taskOwnerAGV.taskDispatchModule.RemoveTaskFromQueue(task_name);
            }

            return Ok("done");
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.AGV;
using VMSystem.VMS;
using Newtonsoft.Json;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVController : ControllerBase
    {   //api/VmsManager/AGVStatus?AGVName=agvname
        [HttpPost("AGVStatus")]
        public async Task<IActionResult> AGVStatus(string AGVName, AGV_MODEL Model, RunningStatus status)
        {
            if (!VMSManager.TryGetAGV(AGVName, Model, out IAGV agv))
            {
                return Ok(new
                {
                    ReturnCode = 1,
                    Message = $"VMS System Not Found AGV With Name ={AGVName} "
                });
            }
            else
            {
                agv.UpdateAGVStates(status);

                return Ok(new
                {
                    ReturnCode = 0,
                    Message = ""
                });
            }
        }

        [HttpPost("TaskFeedback")]
        public async Task<IActionResult> TaskFeedback(string AGVName, AGV_MODEL Model, [FromBody] FeedbackData feedbackData)
        {
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                int confirmed_code = agv.taskDispatchModule.TaskFeedback(feedbackData, out string message);
                return Ok(new { ReturnCode = confirmed_code, Message = message });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

        [HttpPost("OnlineReq")]
        public async Task<IActionResult> OnlineRequest(string AGVName, int tag)
        {
            if (VMSManager.TryGetAGV(AGVName, 0, out var agv))
            {
                agv.online_state = ONLINE_STATE.ONLINE;
                return Ok(new { ReturnCode = 0, Message = "" });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

        [HttpPost("OfflineReq")]
        public async Task<IActionResult> OfflineRequest(string AGVName)
        {
            if (VMSManager.TryGetAGV(AGVName, 0, out var agv))
            {
                agv.online_state = ONLINE_STATE.OFFLINE;
                return Ok(new { ReturnCode = 0, Message = "" });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }



        //api/VmsManager/OnlineMode
        [HttpGet("OnlineMode")]
        public async Task<IActionResult> OnlineStatusQuery(string AGVName, AGV_MODEL Model = AGV_MODEL.UNKNOWN)
        {
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                OnlineModeQueryResponse response = new OnlineModeQueryResponse()
                {
                    RemoteMode = REMOTE_MODE.ONLINE,
                    TimeStamp = DateTime.Now.ToString()
                };

                agv.connected = true;
                return Ok(response);
            }
            else
            {
                OnlineModeQueryResponse response = new OnlineModeQueryResponse()
                {
                    RemoteMode = REMOTE_MODE.OFFLINE,
                    TimeStamp = DateTime.Now.ToString()
                };
                return Ok(response);
            }
        }

    }
}

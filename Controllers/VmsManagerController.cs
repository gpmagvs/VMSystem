using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.TASK;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Text;
using VMSystem.AGV;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Log;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VmsManagerController : ControllerBase
    {
        //api/VmsManager/AGVStatus
        [HttpPost("AGVStatus")]
        public async Task<IActionResult> AGVStatus(RunningStatus status)
        {
            return Ok();
        }

        [HttpGet("/ws/VMSStatus")]
        public async Task GetVMSStatus()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var websocket_client = await HttpContext.WebSockets.AcceptWebSocketAsync();

                while (websocket_client.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        await Task.Delay(200);
                        byte[] rev_buffer = new byte[4096];
                        websocket_client.ReceiveAsync(new ArraySegment<byte>(rev_buffer), CancellationToken.None);
                        var data = VMSManager.GetVMSViewData();
                        await websocket_client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data))), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

                    }
                    catch (Exception ex)
                    {
                        break;
                    }
                }
                Console.WriteLine($"Websocket Client Disconnect-{websocket_client.State}");
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        [HttpPost("ExecuteTask")]
        public async Task<IActionResult> ExecuteTask(clsTaskDto taskData)
        {
            LOG.Critical($"Get Task Data Transfer Object : {taskData.DesignatedAGVName}");
            bool Confirm = VMSManager.TryRequestAGVToExecuteTask(ref taskData, out IAGV agv);
            if (Confirm)
            {
                taskData.DesignatedAGVName = agv.Name;
            }

            return Ok(new { Confirm = Confirm, AGV = agv, taskData });
        }

        [HttpPost("TaskFeedback")]
        public async Task<IActionResult> TaskFeedback([FromBody] object feedbackDataJson)
        {
            var feedbackData = JsonConvert.DeserializeObject<FeedbackData>(feedbackDataJson.ToString());
            int confirmed_code = VMSManager.TaskFeedback(feedbackData);
            return Ok(new { Return_Code = confirmed_code });
        }



        [HttpGet("OnlineRequet")]
        public async Task<IActionResult> OnlineRequet(string agv_name)
        {
            Console.WriteLine($"AGV-{agv_name}要求上線");
            var agv = VMSManager.SearchAGVByName(agv_name);
            if (agv == null)
            {
                Console.WriteLine($"找不到{agv_name}可以上線");
                return Ok(new { Return_Code = 404 });
            }
            bool online_success = agv.Online(out string msg);
            return Ok(new clsAPIRequestResult { Success = online_success, Message = msg });
        }

        [HttpGet("OfflineRequet")]
        public async Task<IActionResult> OfflineRequet(string agv_name)
        {
            Console.WriteLine($"AGV-{agv_name}要求下線");
            var agv = VMSManager.SearchAGVByName(agv_name);
            if (agv == null)
            {
                Console.WriteLine($"找不到{agv_name}可以下線");
                return Ok(new { Return_Code = 404 });
            }
            bool offline_success = agv.Offline(out string msg);
            return Ok(new clsAPIRequestResult { Success = offline_success, Message = msg });
        }





    }
}

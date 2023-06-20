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
using static AGVSystemCommonNet6.Abstracts.CarComponent;
using static AGVSystemCommonNet6.clsEnums;
using System.Xml.Linq;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VmsManagerController : ControllerBase
    {
        //api/VmsManager/AGVStatus?AGVName=agvname
        [HttpPost("AGVStatus")]
        public async Task<IActionResult> AGVStatus(string AGVName, AGV_MODEL Model,RunningStatus status)
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
                _ = agv.SaveStateToDatabase(new AGVSystemCommonNet6.clsAGVStateDto
                {
                    AGV_Name = AGVName,
                    BatteryLevel = status.Electric_Volume[0],
                    OnlineStatus = agv.online_state,
                    MainStatus = status.AGV_Status,
                    CurrentCarrierID = status.CSTID.Length == 0 ? "" : status.CSTID[0],
                    CurrentLocation = status.Last_Visited_Node.ToString(),
                    Theta = status.Coordination.Theta,
                    Connected = true,
                    Model = agv.model
                });
                agv.states = status;
                return Ok(new
                {
                    ReturnCode = 0,
                    Message = ""
                });
            }
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

        //api/VmsManager/OnlineMode
        [HttpGet("OnlineMode")]
        public async Task<IActionResult> OnlineStatusQuery(string AGVName, AGV_MODEL Model = AGV_MODEL.UNKNOWN)
        {
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                OnlineModeQueryResponse response = new OnlineModeQueryResponse()
                {
                    RemoteMode = REMOTE_MODE.OFFLINE,
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

        [HttpGet("OnlineRequet")]
        public async Task<IActionResult> OnlineRequet(string agv_name, AGV_MODEL model = AGV_MODEL.FORK_AGV)
        {

            Console.WriteLine($"AGV-{agv_name}要求上線");

            if (VMSManager.TryGetAGV(agv_name, model, out IAGV agv))
            {
                bool online_success = agv.Online(out string msg);
                return Ok(new clsAPIRequestResult { Success = online_success, Message = msg });
            }
            else
            {
                return Ok(new clsAPIRequestResult { Success = false, Message = "AGV Not Found" });
            }
        }

        [HttpGet("OfflineRequet")]
        public async Task<IActionResult> OfflineRequet(string agv_name, AGV_MODEL model = AGV_MODEL.FORK_AGV)
        {
            Console.WriteLine($"AGV-{agv_name}要求下線");

            if (VMSManager.TryGetAGV(agv_name, model, out IAGV agv))
            {
                bool online_success = agv.Offline(out string msg);
                return Ok(new clsAPIRequestResult { Success = online_success, Message = msg });
            }
            else
            {
                return Ok(new clsAPIRequestResult { Success = false, Message = "AGV Not Found" });
            }
        }

    }
}

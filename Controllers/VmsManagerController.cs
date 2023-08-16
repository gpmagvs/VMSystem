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
using System;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VmsManagerController : ControllerBase
    {

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




        [HttpGet("OnlineRequet")]
        public async Task<IActionResult> OnlineRequet(string agv_name, AGV_MODEL model = AGV_MODEL.FORK_AGV)
        {
            Console.WriteLine($"要求 {agv_name}上線 ");
            if (VMSManager.TryGetAGV(agv_name, model, out IAGV agv))
            {
                try
                {
                    bool online_success = agv.Online(out string msg);
                    return Ok(new { ReturnCode = 0, Message = msg });
                }
                catch (Exception ex)
                {
                    return Ok(new { ReturnCode = 404, Message = ex.Message });

                }
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

        [HttpGet("OfflineRequet")]
        public async Task<IActionResult> OfflineRequet(string agv_name, AGV_MODEL model = AGV_MODEL.FORK_AGV)
        {
            Console.WriteLine($"AGV-{agv_name}要求下線");

            if (VMSManager.TryGetAGV(agv_name, model, out IAGV agv))
            {
                if (agv.options.Simulation)
                {
                    agv.UpdateAGVStates(new RunningStatus
                    {
                        AGV_Status = MAIN_STATUS.IDLE,
                        Last_Visited_Node = agv.states.Last_Visited_Node
                    });
                }

                bool online_success = agv.Offline(out string msg);
                return Ok(new { ReturnCode = 0, Message = msg });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

    }
}

using AGVSystemCommonNet6.HttpTools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Text;
using VMSystem.AGV;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Log;
using static AGVSystemCommonNet6.Vehicle_Control.CarComponent;
using static AGVSystemCommonNet6.clsEnums;
using System.Xml.Linq;
using System;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VmsManagerController : ControllerBase
    {

        [HttpGet("/ws/VMSStatus")]
        public async Task GetVMSStatus()
        {
            await WebsocketClientMiddleware.ClientRequest(HttpContext, WebsocketClientMiddleware.WS_DATA_TYPE.VMSStatus);
        }

        [HttpGet("AGVStatus")]
        public async Task<IActionResult> GetAGVStatus()
        {
            return Ok(VMSManager.AGVStatueDtoStored.Values.ToArray());
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
            bool online_success = false;
            string msg = string.Empty;

            if (VMSManager.TryGetAGV(agv_name, model, out IAGV agv))
            {
                try
                {
                    if (agv.options.Simulation)
                    {
                        agv.online_state = clsEnums.ONLINE_STATE.ONLINE;
                        online_success = true;
                    }
                    else
                    {
                        online_success = agv.AGVOnlineFromAGVS(out msg);
                    }
                    return Ok(new { ReturnCode = online_success ? 0 : 404, Message = msg });
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
            string msg = string.Empty;
            if (VMSManager.TryGetAGV(agv_name, model, out IAGV agv))
            {
                if (agv.options.Simulation)
                {
                    agv.AgvSimulation.CancelTask();
                    agv.online_state = ONLINE_STATE.OFFLINE;
                    agv.states.AGV_Status = MAIN_STATUS.IDLE;
                }
                else
                {
                    bool online_success = agv.AGVOfflineFromAGVS(out msg);
                }
                return Ok(new { ReturnCode = 0, Message = msg });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }


    }
}

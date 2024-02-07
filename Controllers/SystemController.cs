using AGVSystemCommonNet6.AGVDispatch.RunMode;
using AGVSystemCommonNet6.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SystemController : ControllerBase
    {
        [HttpGet("/ws/VMSAliveCheck")]
        public async Task AliveCheck()
        {
            await WebsocketClientMiddleware.ClientRequest(HttpContext, WebsocketClientMiddleware.WS_DATA_TYPE.VMSAliveCheck);

        }

        [HttpGet("VMSAliveCheck")]
        public async Task<IActionResult> AliveCheckHttp()
        {
            return Ok(true);
        }


        /// <summary>
        /// 院運模式
        /// </summary>
        /// <param name="mode"></param>
        /// <returns></returns>
        [HttpPost("RunMode")]
        public async Task<IActionResult> RunMode(RUN_MODE mode, bool? forecing_change = false)
        {
            bool confirm = SystemModes.RunModeSwitch(mode, out string message);
            return Ok(new { confirm = confirm, message });
        }

        [HttpGet("T1Timeout_Simulation_OnlineModeQuery")]
        public async Task Simulation(bool enable)
        {
            VMSManager.Tests.AGVOnlineModeQueryT1TimeoutSimulationFlag = enable;
        }

        [HttpGet("T1Timeout_Simulation_RunningStatusReport")]
        public async Task T1Timeout_Simulation_RunningStatusReport(bool enable)
        {
            VMSManager.Tests.AGVRunningStatusReportT1TimeoutSimulationFlag = enable;
        }

        [HttpGet("T1Timeout_Simulation_TaskFeedback")]
        public async Task T1Timeout_Simulation_TaskFeedback(bool enable)
        {
            VMSManager.Tests.AGVTaskFeedfackReportT1TimeoutSimulationFlag = enable;
        }
    }
}

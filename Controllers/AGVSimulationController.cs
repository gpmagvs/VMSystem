using AGVSystemCommonNet6;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.AGV;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVSimulationController : ControllerBase
    {
        [HttpGet("GetSimulationParameters")]
        public async Task<Dictionary<string, clsAGVSimulation.clsAGVSimulationParameters>> GetSimulationParameters()
        {
            return VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => agv.AgvSimulation.parameters);
        }

        [HttpPost("ModifySimulationParamters")]
        public async Task<IActionResult> ModifySimulationParameters([FromBody] clsAGVSimulation.clsAGVSimulationParameters parameters, string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            agv.AgvSimulation.parameters = parameters;
            return Ok();
        }


        [HttpPost("SetTag")]
        public async Task<IActionResult> SetTag(string AGVName, int tag)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            agv.AgvSimulation.SetTag(tag);
            return Ok();
        }

        [HttpPost("MoveUp")]
        public async Task<IActionResult> MoveUp(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            var _oriCoor = agv.AgvSimulation.runningSTatus.Coordination.Clone();
            agv.AgvSimulation.runningSTatus.Coordination.Y = _oriCoor.Y + 0.01;
            return Ok();
        }

        [HttpPost("MoveDown")]
        public async Task<IActionResult> MoveDown(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            var _oriCoor = agv.AgvSimulation.runningSTatus.Coordination.Clone();
            agv.AgvSimulation.runningSTatus.Coordination.Y = _oriCoor.Y - 0.01;
            return Ok();
        }


        [HttpPost("MoveRight")]
        public async Task<IActionResult> MoveRight(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            var _oriCoor = agv.AgvSimulation.runningSTatus.Coordination.Clone();
            agv.AgvSimulation.runningSTatus.Coordination.X = _oriCoor.X + 0.01;
            return Ok();
        }

        [HttpPost("MoveLeft")]
        public async Task<IActionResult> MoveLeft(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            var _oriCoor = agv.AgvSimulation.runningSTatus.Coordination.Clone();
            agv.AgvSimulation.runningSTatus.Coordination.X = _oriCoor.X - 0.01;
            return Ok();
        }

        [HttpPost("TurnRight")]
        public async Task<IActionResult> TurnRight(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            var _oriCoor = agv.AgvSimulation.runningSTatus.Coordination.Clone();
            agv.AgvSimulation.runningSTatus.Coordination.Theta = _oriCoor.Theta - 1;
            return Ok();
        }


        [HttpPost("TurnLeft")]
        public async Task<IActionResult> TurnLeft(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            var _oriCoor = agv.AgvSimulation.runningSTatus.Coordination.Clone();
            agv.AgvSimulation.runningSTatus.Coordination.Theta = _oriCoor.Theta + 1;
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="AGVName"></param>
        /// <param name="status"> 1:IDLE, 2:RUN, 3:DOWN, 4:Charging,</param>
        /// <returns></returns>
        [HttpPost("SetMainStatus")]
        public async Task<IActionResult> SetMainStatus(string AGVName, MAIN_STATUS status)
        {

            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            agv.AgvSimulation.runningSTatus.AGV_Status = status;
            return Ok();
        }
        [HttpPost("SetBatteryLevel")]
        public async Task<IActionResult> SetBatteryLevel(string AGVName, double lv)
        {

            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            agv.AgvSimulation.runningSTatus.Electric_Volume[0] = lv;
            return Ok();
        }
        [HttpGet("UnRecoveryAlarmSimulation")]
        public async Task<IActionResult> UnRecoveryAlarmSimulation(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();

            agv.AgvSimulation.UnRecoveryAlarmRaise();
            return Ok();
        }

        [HttpGet("EMO")]
        public async Task<IActionResult> EMO(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();

            agv.AgvSimulation.EMO();
            return Ok();
        }

        [HttpGet("Initialize")]
        public async Task<IActionResult> Initialize(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();

            agv.AgvSimulation.Initialize();
            return Ok();
        }


        /// <summary>
        /// 模擬裝載貨物
        /// </summary>
        /// <param name="AGVName"></param>
        /// <param name="cargoID"></param>
        /// <returns></returns>
        [HttpGet("CargoMounted")]
        public async Task<IActionResult> CargoMounted(string AGVName, string cargoID)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();

            agv.AgvSimulation.MounteCargo(cargoID);
            return Ok();
        }

        /// <summary>
        /// 模擬移除貨物
        /// </summary>
        /// <param name="AGVName"></param>
        /// <returns></returns>
        [HttpGet("CargoRemove")]
        public async Task<IActionResult> CargoRemove(string AGVName)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();

            agv.AgvSimulation.RemoveCargo();
            return Ok();
        }
    }
}

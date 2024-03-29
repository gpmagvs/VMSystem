﻿using AGVSystemCommonNet6;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.AGV;
using VMSystem.VMS;

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
        public async Task<IActionResult> SetTag(string AGVName,int tag)
        {
            IAGV agv = VMSManager.GetAGVByName(AGVName);
            if (agv == null)
                return BadRequest();
            agv.AgvSimulation.runningSTatus.Last_Visited_Node = tag;
            var _mapPoint=StaMap.GetPointByTagNumber(tag);
            agv.AgvSimulation.runningSTatus.Coordination.X = _mapPoint.X;
            agv.AgvSimulation.runningSTatus.Coordination.Y = _mapPoint.Y;
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
    }
}

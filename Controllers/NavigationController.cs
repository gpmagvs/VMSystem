using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NavigationController : ControllerBase
    {
        [HttpGet("AddPartsReplaceworkstationTag")]
        public async Task<IActionResult> AddPartsReplaceworkstationTag(int workstationTag)
        {
            Dispatch.DispatchCenter.AddWorkStationInPartsReplacing(workstationTag);
            return Ok(new AGVSystemCommonNet6.Microservices.ResponseModel.clsResponseBase(true, ""));
        }

        [HttpGet("RemovePartsReplaceworkstationTag")]
        public async Task<IActionResult> RemovePartsReplaceworkstationTag(int workstationTag)
        {
            Dispatch.DispatchCenter.RemoveWorkStationInPartsReplacing(workstationTag);
            return Ok(new AGVSystemCommonNet6.Microservices.ResponseModel.clsResponseBase(true, ""));
        }
    }
}

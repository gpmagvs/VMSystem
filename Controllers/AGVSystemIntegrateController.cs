using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVSystemIntegrateController : ControllerBase
    {
        [HttpPost("ReportAGVStatus")]
        public async Task<IActionResult> ReportAGVLocation([FromBody] Dictionary<string, string> payload)
        {
            try
            {
                string _AGVName = payload["AGVName"];
                string _Location = payload["Location"];
                VMSManager.UpdatePartsAGVInfo(_AGVName, _Location);
                return Ok();
            }
            catch (Exception ex)
            {
                LOG.ERROR(ex.Message);
                return new BadRequestResult();
            }
        }
    }
}

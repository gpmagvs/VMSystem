using AGVSystemCommonNet6;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VMSystem.VMS;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVSystemIntegrateController : ControllerBase
    {
        ILogger logger;
        public AGVSystemIntegrateController(ILogger logger)
        {
            this.logger = logger;
        }
        [HttpPost("ReportAGVStatus")]
        public async Task<IActionResult> ReportAGVLocation([FromBody] Dictionary<string, string> payload)
        {
            try
            {
                string _AGVName = payload["AGVName"];
                string _Location = payload["Location"];
                //VMSManager.UpdatePartsAGVInfo(_AGVName, _Location);
                return Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
                return new BadRequestResult();
            }
        }
    }
}

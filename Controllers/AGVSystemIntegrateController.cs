using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVSystemIntegrateController : ControllerBase
    {
        [HttpPost("ReportAGVStatus")]
        public async Task<IActionResult> ReportAGVLocation([FromBody] Dictionary<string, string> payload)
        {
            string _AGVName = payload["AGVName"];
            string _Location = payload["Location"];
            Console.WriteLine($"{_AGVName} locate in {_Location}");
            return Ok();
        }
    }
}

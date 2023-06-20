using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MapController : ControllerBase
    {
        [HttpGet("Reload")]
        public async Task<IActionResult> Reload(string map_file)
        {
            StaMap.Download(map_file);
            return Ok(true);
        }
    }
}

﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MapController : ControllerBase
    {
        [HttpGet("Reload")]
        public async Task<IActionResult> Reload()
        {
            StaMap.Download();
            return Ok(true);
        }
    }
}

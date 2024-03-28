using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVOTAController : ControllerBase
    {

        [HttpGet("GetNewestVersion")]
        public async Task<IActionResult> GetNewestVersion()
        {
            return Ok(AGV.Update.AGVProgramUpdateHelper.GetNewestUpdateFile());
        }
        [HttpGet("DownloadTest")]
        public async Task DownloadTest()
        {
            using (HttpClient client = new HttpClient())
            using (HttpResponseMessage response = await client.GetAsync("http://localhost:5036/AGVUpdateFiles/test.7z", HttpCompletionOption.ResponseHeadersRead))
            using (Stream streamToReadFrom = await response.Content.ReadAsStreamAsync())
            {
                response.EnsureSuccessStatusCode();  // 确保响应成功
                string fileToWriteTo = Path.GetFullPath("C:\\AGVS\\test.7z");
                using (Stream streamToWriteTo = System.IO.File.Open(fileToWriteTo, FileMode.CreateNew))
                {
                    await streamToReadFrom.CopyToAsync(streamToWriteTo);
                }
            }
        }
    }
}

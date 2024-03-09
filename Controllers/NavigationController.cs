using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NavigationController : ControllerBase
    {

        /// <summary>
        /// 收集所有AGV當前的導航路徑
        /// </summary>
        /// <returns></returns>
        [HttpGet("/ws/AGVNaviPathsInfo")]
        public async Task NaviPaths()
        {
            await WebsocketClientMiddleware.middleware.HandleWebsocketClientConnectIn(HttpContext);
        }

    }
}

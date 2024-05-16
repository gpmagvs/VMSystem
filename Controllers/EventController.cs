using AGVSystemCommonNet6.Notify;
using Microsoft.AspNetCore.Mvc;

namespace AGVSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {

        public EventController()
        {
        }
        [HttpGet]
        public async Task GetEventsAsync()
        {
            await NotifyServiceHelper.WebsocketNotification(HttpContext);
        }

    }
}

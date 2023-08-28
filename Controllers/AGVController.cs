using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.AGV;
using VMSystem.VMS;
using Newtonsoft.Json;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.AGVDispatch.Model;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVController : ControllerBase
    {   //api/VmsManager/AGVStatus?AGVName=agvname
        [HttpPost("AGVStatus")]
        public async Task<IActionResult> AGVStatus(string AGVName, AGV_MODEL Model, clsRunningStatus status)
        {
            if (!VMSManager.TryGetAGV(AGVName, Model, out IAGV agv))
            {
                return Ok(new
                {
                    ReturnCode = 1,
                    Message = $"VMS System Not Found AGV With Name ={AGVName} "
                });
            }
            else
            {

                agv.states = status;
                return Ok(new
                {
                    ReturnCode = 0,
                    Message = ""
                });
            }
        }

        [HttpPost("TaskFeedback")]
        public async Task<IActionResult> TaskFeedback(string AGVName, AGV_MODEL Model, [FromBody] FeedbackData feedbackData)
        {
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                int confirmed_code = await agv.taskDispatchModule.TaskFeedback(feedbackData);
                return Ok(new { ReturnCode = confirmed_code, Message = "" });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

        [HttpPost("OnlineReq")]
        public async Task<IActionResult> OnlineRequest(string AGVName, int tag)
        {
            string errMsg = "";
            ALARMS aramCode = ALARMS.NONE;
            if (VMSManager.TryGetAGV(AGVName, 0, out var agv))
            {
                bool isOnlineTagExist = StaMap.TryGetPointByTagNumber(tag, out var point);
                if (isOnlineTagExist)
                {
                    double agvLocOffset = point.CalculateDistance(agv.states.Coordination.X, agv.states.Coordination.Y);
                    if (agvLocOffset > 1)
                    {
                        aramCode = ALARMS.GET_ONLINE_REQ_BUT_AGV_LOCATION_IS_TOO_FAR_FROM_POINT;
                        errMsg = "AGV上線之位置與圖資差距過大";
                    }
                    else
                    {
                        StaMap.RegistPoint(agv.Name, point, out string err_msg);
                        agv.online_state = ONLINE_STATE.ONLINE;
                        return Ok(new { ReturnCode = 0, Message = "" });
                    }
                }
                else
                {
                    aramCode = ALARMS.GET_ONLINE_REQ_BUT_AGV_LOCATION_IS_NOT_EXIST_ON_MAP;
                    errMsg = $"{tag}不存在於目前的地圖";
                }
            }
            else
            {
                aramCode = ALARMS.GET_ONLINE_REQ_BUT_AGV_IS_NOT_REGISTED;
                errMsg = $"{AGVName} Not Registed In ASGVSystem";
            }
            if (aramCode != ALARMS.NONE)
                AlarmManagerCenter.AddAlarm(aramCode, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING);
            return Ok(new { ReturnCode = errMsg == "" && aramCode == ALARMS.NONE ? 0 : 1, Message = errMsg });
        }

        [HttpPost("OfflineReq")]
        public async Task<IActionResult> OfflineRequest(string AGVName)
        {
            if (VMSManager.TryGetAGV(AGVName, 0, out var agv))
            {
                agv.online_state = ONLINE_STATE.OFFLINE;
                return Ok(new { ReturnCode = 0, Message = "" });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }



        //api/VmsManager/OnlineMode
        [HttpGet("OnlineMode")]
        public async Task<IActionResult> OnlineStatusQuery(string AGVName, AGV_MODEL Model = AGV_MODEL.UNKNOWN)
        {
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                OnlineModeQueryResponse response = new OnlineModeQueryResponse()
                {
                    RemoteMode = REMOTE_MODE.ONLINE,
                    TimeStamp = DateTime.Now.ToString()
                };
                agv.connected = true;

                return Ok(response);
            }
            else
            {
                OnlineModeQueryResponse response = new OnlineModeQueryResponse()
                {
                    RemoteMode = REMOTE_MODE.OFFLINE,
                    TimeStamp = DateTime.Now.ToString()
                };
                return Ok(response);
            }
        }

    }
}

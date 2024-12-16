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
using AGVSystemCommonNet6.DATABASE;
using System.Diagnostics;
using VMSystem.TrafficControl;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using static VMSystem.AGV.TaskDispatch.Tasks.clsLeaveFromWorkStationConfirmEventArg;
using VMSystem.AGV.TaskDispatch.Tasks;
using NLog;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AGVController : ControllerBase
    {   //api/VmsManager/AGVStatus?AGVName=agvname
        [HttpPost("AGVStatus")]
        public async Task<IActionResult> AGVStatus(string AGVName, AGV_TYPE Model, clsRunningStatus status)
        {
            if (VMSManager.GetAGVByName(AGVName, out var agv))
            {
                agv.states = status;
                return Ok(new
                {
                    ReturnCode = 0,
                    Message = ""
                });

            }
            else
            {
                return Ok(new
                {
                    ReturnCode = 1,
                    Message = $"VMS System Not Found AGV With Name ={AGVName} "
                });
            }
        }

        [HttpPost("TaskFeedback")]
        public async Task<IActionResult> TaskFeedback(string AGVName, AGV_TYPE Model, [FromBody] FeedbackData feedbackData)
        {
            try
            {
                if (VMSManager.GetAGVByName(AGVName, out var agv))
                {
                    bool feedbackConfirmed = await agv.TaskExecuter.HandleVehicleTaskStatusFeedback(feedbackData);
                    if (!feedbackConfirmed)
                    {
                        return Ok(new { ReturnCode = 2, Message = "Feedback Not Confirmed" });
                    }
                    int confirmed_code = await agv.taskDispatchModule.TaskFeedback(feedbackData);
                    return Ok(new { ReturnCode = confirmed_code, Message = "" });
                }
                else
                {
                    return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
                }
            }
            catch (Exception ex)
            {
                await AlarmManagerCenter.AddAlarmAsync(ALARMS.AGV_TaskFeedback_ERROR, Equipment_Name: AGVName);
                return Ok(new { ReturnCode = 1, Message = ex.Message });
            }
        }

        [HttpPost("ReportMeasure")]
        public async Task<IActionResult> ReportMeasure(string AGVName, AGV_TYPE Model, [FromBody] clsMeasureResult measureResult)
        {
            if (VMSManager.GetAGVByName(AGVName, out var agv))
            {
                (agv.taskDispatchModule.OrderHandler as MeasureOrderHandler).MeasureResultFeedback(measureResult);
                return Ok(new { ReturnCode = 0, Message = "" });
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
            if (VMSManager.GetAGVByName(AGVName, out var agv))
            {
                if (agv.model == AGV_TYPE.INSPECTION_AGV)
                {
                    tag = agv.states.Last_Visited_Node;
                }
                bool isOnlineTagExist = StaMap.TryGetPointByTagNumber(tag, out var point);
                if (isOnlineTagExist)
                {
                    double agvLocOffset = point.CalculateDistance(agv.states.Coordination.X, agv.states.Coordination.Y);
                    if (agvLocOffset > 1.50) //0.2m
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
            {
                await AlarmManagerCenter.AddAlarmAsync(aramCode, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING);
                return Ok(new { ReturnCode = errMsg == "" && aramCode == ALARMS.NONE ? 0 : 1, Message = errMsg }); ;
                agv.AGVOfflineFromAGVS(out string msg);
            }
            return Ok(new { ReturnCode = errMsg == "" && aramCode == ALARMS.NONE ? 0 : 1, Message = errMsg });
        }

        [HttpPost("OfflineReq")]
        public async Task<IActionResult> OfflineRequest(string AGVName)
        {
            if (VMSManager.GetAGVByName(AGVName, out var agv))
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
        public async Task<IActionResult> OnlineStatusQuery(string AGVName, AGV_TYPE Model = AGV_TYPE.Any)
        {
            if (VMSManager.GetAGVByName(AGVName, out var agv))
            {
                OnlineModeQueryResponse response = new OnlineModeQueryResponse()
                {
                    RemoteMode = agv.online_mode_req == ONLINE_STATE.OFFLINE ? REMOTE_MODE.OFFLINE : REMOTE_MODE.ONLINE,
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





        //api/VmsManager/OnlineMode
        [HttpGet("CarrierVirtualID")]
        public async Task<IActionResult> GetCarrierVirtualID(string AGVName, AGV_TYPE Model = AGV_TYPE.Any)
        {
            if (VMSManager.GetAGVByName(AGVName, out var agv))
            {
                var virtual_id = $"UN{DateTime.Now.ToString("yyMMddHHmmssfff")}";
                return Ok(new clsCarrierVirtualIDResponseWebAPI
                {
                    TimeStamp = DateTime.Now,
                    VirtualID = virtual_id
                });
            }
            else
            {
                throw new Exception();
            }
        }

        [HttpPost("LeaveWorkStationRequest")]
        public async Task<IActionResult> AGVLeaveWorkStationRequest(string AGVName, int EQTag)
        {
            try
            {
                (bool accept, string message) = await TrafficControlCenter.AGVLeaveWorkStationRequest(AGVName, EQTag);
                return Ok(new
                {
                    confirm = accept,
                    message = message
                });
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

    }
}

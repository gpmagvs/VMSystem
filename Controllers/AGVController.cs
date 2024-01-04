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
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.DATABASE;
using System.Diagnostics;

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
            try
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
            catch (Exception ex)
            {
                await AlarmManagerCenter.AddAlarmAsync(ALARMS.AGV_TaskFeedback_ERROR, Equipment_Name: AGVName);
                LOG.Critical(ex);
                return Ok(new { ReturnCode = 1, Message = ex.Message });
            }
        }

        [HttpPost("ReportMeasure")]
        public async Task<IActionResult> ReportMeasure(string AGVName, AGV_MODEL Model, [FromBody] clsMeasureResult measureResult)
        {
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                string BayName = StaMap.GetBayNameByMesLocation(measureResult.location);
                measureResult.AGVName = AGVName;
                measureResult.BayName = BayName;
                LOG.INFO($"AGV-{AGVName} Report Measure Data: {measureResult.ToJson()}");
                _ = Task.Run(() =>
                {
                    using (var database = new AGVSDatabase())
                    {
                        try
                        {
                            database.tables.InstrumentMeasureResult.Add(measureResult);
                            database.SaveChanges();
                        }
                        catch (Exception ex)
                        {
                            LOG.ERROR(ex.Message, ex);
                             AlarmManagerCenter.AddAlarmAsync(ALARMS.Save_Measure_Data_to_DB_Fail, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING);
                        }
                    }
                });
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
            if (VMSManager.TryGetAGV(AGVName, 0, out var agv))
            {
                if (agv.model == AGV_MODEL.INSPECTION_AGV)
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
                agv.AGVOfflineFromAGVS(out string msg);
                await AlarmManagerCenter.AddAlarmAsync(aramCode, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING);
            }
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





        //api/VmsManager/OnlineMode
        [HttpGet("CarrierVirtualID")]
        public async Task<IActionResult> GetCarrierVirtualID(string AGVName, AGV_MODEL Model = AGV_MODEL.UNKNOWN)
        {
            LOG.TRACE($"{AGVName} Query Carrier Virtual ID.");
            if (VMSManager.TryGetAGV(AGVName, Model, out var agv))
            {
                var virtual_id = $"UN{DateTime.Now.ToString("yyMMddHHmmssfff")}";
                LOG.TRACE($"{AGVName} Query Carrier Virtual ID.={virtual_id}");
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



    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Alarm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VMSystem.AGV;
using VMSystem.BackgroundServices;
using VMSystem.Services;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static VMSystem.AGV.clsGPMInspectionAGV;

namespace VMSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VmsManagerController : ControllerBase
    {
        private VehicleOnlineBySystemService _onlineService;
        ILogger<VmsManagerController> logger;
        public VmsManagerController(VehicleOnlineBySystemService onlineService, ILogger<VmsManagerController> logger)
        {
            _onlineService = onlineService;
            this.logger = logger;
        }

        [HttpGet("AGVStatus")]
        public async Task<IActionResult> GetAGVStatus()
        {
            return Ok(VehicleStateService.AGVStatueDtoStored.Values.ToArray());
        }

        [HttpPost("ExecuteTask")]
        public async Task<IActionResult> ExecuteTask(clsTaskDto taskData)
        {
            logger.LogInformation($"Get Task Data Transfer Object : {taskData.DesignatedAGVName}");
            bool Confirm = VMSManager.TryRequestAGVToExecuteTask(ref taskData, out IAGV agv);
            if (Confirm)
            {
                taskData.DesignatedAGVName = agv.Name;
            }

            return Ok(new { Confirm = Confirm, AGV = agv, taskData });
        }




        [HttpGet("OnlineRequet")]
        public async Task<IActionResult> OnlineRequet(string agv_name, clsEnums.AGV_TYPE model = clsEnums.AGV_TYPE.FORK)
        {
            (ALARMS alarmCode, string message) check_result = _onlineService.OnlineRequest(agv_name, out _);
            return Ok(new { ReturnCode = check_result.alarmCode, Message = check_result.message });

            logger.LogInformation($"用戶要求 {agv_name}上線 ");
            bool online_success = false;
            string msg = string.Empty;

            if (VMSManager.TryGetAGV(agv_name, out IAGV agv))
            {
                try
                {
                    online_success = agv.AGVOnlineFromAGVS(out msg);

                    return Ok(new { ReturnCode = online_success ? 0 : 404, Message = msg });
                }
                catch (Exception ex)
                {
                    return Ok(new { ReturnCode = 404, Message = ex.Message });

                }
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

        [HttpGet("OfflineRequet")]
        public async Task<IActionResult> OfflineRequet(string agv_name, clsEnums.AGV_TYPE model = clsEnums.AGV_TYPE.FORK)
        {
            logger.LogInformation($"用戶要求 {agv_name}下線 ");
            string msg = string.Empty;
            if (VMSManager.TryGetAGV(agv_name, out IAGV agv))
            {
                if (agv.options.Simulation)
                {
                    agv.AgvSimulation.CancelTask();
                    agv.online_state = ONLINE_STATE.OFFLINE;
                    agv.states.AGV_Status = MAIN_STATUS.IDLE;
                }
                else
                {
                    bool online_success = agv.AGVOfflineFromAGVS(out msg);
                }
                return Ok(new { ReturnCode = 0, Message = msg });
            }
            else
            {
                return Ok(new { ReturnCode = 1, Message = "AGV Not Found" });
            }
        }

        [HttpPost("AGVLocating")]
        public async Task<IActionResult> AGVLocating([FromBody] clsLocalizationVM localizationVM, string agv_name)
        {
            var result = await VMSManager.TryLocatingAGVAsync(agv_name, localizationVM);
            return Ok(new { confirm = result.confirm, message = result.message });
        }

        [HttpPost("AddVehicle")]
        public async Task<IActionResult> AddVehicle([FromBody] clsAGVStateDto dto)
        {
            var result = await VMSManager.AddVehicle(dto);
            return Ok(new { confirm = result.confirm, message = result.message });
        }
        [HttpPost("EditVehicle")]
        public async Task<IActionResult> EditVehicle([FromBody] clsAGVStateDto dto, string oriAGVID)
        {
            var result = await VMSManager.EditVehicle(dto, oriAGVID);
            return Ok(new { confirm = result.confirm, message = result.message });
        }
        [HttpDelete("DeleteVehicle")]
        public async Task<IActionResult> DeleteVehicle(string AGV_Name)
        {
            var result = await VMSManager.DeleteVehicle(AGV_Name);
            return Ok(new { confirm = result.confirm, message = result.message });
        }

        [HttpPost("UnregisterFromNetwork")]
        public async Task<IActionResult> UnregisterFromNetwork(string AGV_Name)
        {
            return Ok(await VMSManager.RemoveVehicleFromMap(AGV_Name));
        }
        [HttpPost("StopDeepCharge")]
        public async Task StopDeepCharge(string agvName)
        {
            VMSManager.StopDeepCharge(agvName);
        }
    }
}

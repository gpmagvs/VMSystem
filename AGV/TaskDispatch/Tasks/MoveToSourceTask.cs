using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToSourceTask : MoveTaskDynamicPathPlanV2
    {
        public MoveToSourceTask()
        {
        }

        public MoveToSourceTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public MoveToSourceTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Source;

        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                if (this.Agv.IsAGVHasCargoOrHasCargoID() == true)
                    OrderData.Actual_Carrier_ID = this.Agv.states.CSTID[0];
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode, response.message);
            }
            MCSCIMService.VehicleDepartedReport(Agv.Name, OrderData.From_Station);
            (bool confirmed, ALARMS alarm_code, string message) result = await base.DistpatchToAGV();
            MCSCIMService.VehicleArrivedReport(Agv.Name, OrderData.From_Station);

            return result;
        }
    }
}

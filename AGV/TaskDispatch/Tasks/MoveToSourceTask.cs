using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToSourceTask : MoveTaskDynamicPathPlanV2
    {
        public MoveToSourceTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        { }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Source;

        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport( OrderData.From_Station_Tag, this, OrderData.Action);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode);
            }
            //if (!OrderData.bypass_eq_status_check)
            //{
            //    clsAGVSTaskReportResponse response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(OrderData.From_Station_Tag, ACTION_TYPE.Unload);
            //    if (response == null || response.confirm == false)
            //        return (response.confirm, response.AlarmCode);
            //}
            return await base.DistpatchToAGV();
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using AGVSystemCommonNet6.Notify;
using System.Data;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToDestineTask : MoveTaskDynamicPathPlanV2
    {
        public MoveToDestineTask() : base()
        { }

        public MoveToDestineTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Destine;
        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                if (OrderData.Action == ACTION_TYPE.Carry)
                {
                    var destinePt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                    NotifyServiceHelper.INFO($"{Agv.Name} Start Go To Destine({destinePt.Graph.Display}) Of Carry Order");
                    await AGVSSerivces.TRANSFER_TASK.StartTransferCargoReport(this.Agv.Name, OrderData.From_Station_Tag, OrderData.To_Station_Tag);
                }
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode);
            }
            return await base.DistpatchToAGV();
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using AGVSystemCommonNet6.Notify;
using System.Data;
using VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToDestineTask : MoveTaskDynamicPathPlanV2
    {
        public MoveToDestineTask() : base()
        {

        }
        public MoveToDestineTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }
        public MoveToDestineTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        internal DestineChangeBase DestineChanger { get; set; } = null;

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Destine;
        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                if (OrderData.Action == ACTION_TYPE.Carry)
                {
                    var destinePt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                    NotifyServiceHelper.INFO($"{Agv.Name} Start Go To Destine({destinePt.Graph.Display}) Of Carry Order");
                    bool isSourceAGV = OrderData.IsFromAGV;
                    clsAGVSTaskReportResponse responseOfStartTrasferRpt = await AGVSSerivces.TRANSFER_TASK.StartTransferCargoReport(this.Agv.Name, OrderData.From_Station_Tag, OrderData.To_Station_Tag, OrderData.From_Slot, OrderData.To_Slot, isSourceAGV);
                    if (!responseOfStartTrasferRpt.confirm)
                    {
                        return (false, responseOfStartTrasferRpt.AlarmCode, responseOfStartTrasferRpt.message);
                    }
                }
                if (this.Agv.IsAGVHasCargoOrHasCargoID() == true)
                    OrderData.Actual_Carrier_ID = this.Agv.states.CSTID[0];
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode, response.message);
            }
            if (DestineChanger != null)
            {
                DestineChanger.OnStartChanged += Agv.taskDispatchModule.HandleDestineStartChangeEvent;
                DestineChanger.StartMonitorAsync();

            }
            MCSCIMService.VehicleDepartedReport(Agv.AgvIDStr, OrderData.From_Station);
            (bool confirmed, ALARMS alarm_code, string message) baseResult = await base.DistpatchToAGV();
            MCSCIMService.VehicleArrivedReport(Agv.AgvIDStr, OrderData.destinePortID);
            return baseResult;
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 到目的地放貨
    /// </summary>
    public class LoadAtDestineTask : LoadUnloadTask
    {
        public LoadAtDestineTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
            DestineTag = order.To_Station_Tag;
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.WorkingAtDestination;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Load;

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }

        protected override int GetSlotHeight()
        {
            if (int.TryParse(OrderData.To_Slot, out var height))
                return height;
            else
                return 0;
        }

        protected override void UpdateActionDisplay()
        {
            //終點站放貨
            var equipment = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
            TrafficWaitingState.SetDisplayMessage($"{equipment.Graph.Display}-放貨");
        }

        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                //clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData.need_change_agv ? OrderData.TransferToTag : OrderData.To_Station_Tag, this, OrderData.Action);
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode);
            }
            return await base.DistpatchToAGV();
        }

    }



}

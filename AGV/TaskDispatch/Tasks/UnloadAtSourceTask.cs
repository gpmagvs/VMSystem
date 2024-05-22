using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 在起點設備取貨任務
    /// </summary>
    public class UnloadAtSourceTask : LoadUnloadTask
    {
        public UnloadAtSourceTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
            DestineTag = order.From_Station_Tag;
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.WorkingAtSource;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Unload;

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }

        protected override int GetSlotHeight()
        {
            if (int.TryParse(OrderData.From_Slot, out var height))
                return height;
            else
                return 0;
        }

        protected override void UpdateActionDisplay()
        {
            //終點站放貨
            var equipment = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
            TrafficWaitingState.SetDisplayMessage($"{equipment.Graph.Display}-取貨");
        }

        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                clsAGVSTaskReportResponse response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(OrderData.From_Station_Tag, ACTION_TYPE.Unload);
                if (response == null || response.confirm == false)
                    return (response.confirm, response.AlarmCode);
            }
            return await base.DistpatchToAGV();
        }
    }
}

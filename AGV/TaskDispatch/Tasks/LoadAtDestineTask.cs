using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
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

        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(OrderData.To_Station_Tag, ACTION_TYPE.Load);
            return await base.DistpatchToAGV();
        }

    }



}

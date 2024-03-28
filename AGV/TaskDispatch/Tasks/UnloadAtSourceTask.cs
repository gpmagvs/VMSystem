using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;

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

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.WorkingAtSource;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Unload;

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }
        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(OrderData.From_Station_Tag, ACTION_TYPE.Unload);
            return await base.DistpatchToAGV();
        }
    }
}

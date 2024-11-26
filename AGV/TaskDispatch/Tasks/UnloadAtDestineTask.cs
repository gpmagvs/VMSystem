using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 到目的地取貨
    /// </summary>
    public class UnloadAtDestineTask : LoadUnloadTask
    {
        public UnloadAtDestineTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.WorkingAtDestination;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Unload;

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
            TrafficWaitingState.SetDisplayMessage($"{equipment.Graph.Display}-取貨");
        }

        public override (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) ActionFinishInvoke()
        {
            ReportUnloadCargoFromPortDone();
            return base.ActionFinishInvoke();
        }
    }
}

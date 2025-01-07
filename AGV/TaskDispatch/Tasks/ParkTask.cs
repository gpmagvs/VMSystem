using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class ParkTask : ChargeTask
    {
        public ParkTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }
        public ParkTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage => VehicleMovementStage.ParkAtWorkStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Park;

        public override Task SendTaskToAGV()
        {
            MapPoint stationPt = StaMap.GetPointByTagNumber(this.TaskDonwloadToAGV.Homing_Trajectory.Last().Point_ID);
            UpdateMoveStateMessage($"進入 [{stationPt.Graph.Display}] 停車");

            return base.SendTaskToAGV();
        }
    }
}

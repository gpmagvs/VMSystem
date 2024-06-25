using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class ParkTask : ChargeTask
    {
        public override VehicleMovementStage Stage => VehicleMovementStage.ParkAtWorkStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Park;
        public ParkTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override Task SendTaskToAGV()
        {
            MapPoint stationPt = StaMap.GetPointByTagNumber(this.TaskDonwloadToAGV.Homing_Trajectory.Last().Point_ID);
            UpdateMoveStateMessage($"進入 [{stationPt.Graph.Display}] 停車");

            return base.SendTaskToAGV();
        }
    }
}

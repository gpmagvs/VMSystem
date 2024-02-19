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
    }
}

using AGVSystemCommonNet6.AGVDispatch;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class NormalMoveTask : MoveTask
    {
        public NormalMoveTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.Traveling;

    }
}

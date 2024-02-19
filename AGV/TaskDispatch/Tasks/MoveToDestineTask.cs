using AGVSystemCommonNet6.AGVDispatch;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToDestineTask : MoveTask
    {
        public MoveToDestineTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.Traveling_To_Destine;

    }
}

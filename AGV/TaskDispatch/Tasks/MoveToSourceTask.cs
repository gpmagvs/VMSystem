using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToSourceTask : MoveTask
    {
        public MoveToSourceTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.Traveling_To_Source;
    }
}

using AGVSystemCommonNet6.AGVDispatch;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToDestineTask : MoveTaskDynamicPathPlan
    {
        public MoveToDestineTask() : base()
        {
        }

        public MoveToDestineTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Destine;

    }
}

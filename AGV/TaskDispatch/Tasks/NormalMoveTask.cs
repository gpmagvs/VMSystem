using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class NormalMoveTask : MoveTaskDynamicPathPlan
    {
        public NormalMoveTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling;

    }
}

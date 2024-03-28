using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Microservices.AGVS;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class LoadOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Load;
        protected override async Task HandleAGVActionFinishFeedback()
        {

            if (RunningTask.ActionType == ACTION_TYPE.Load)
            {
                await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(RunningTask.DestineTag, RunningTask.ActionType);
            }

            await base.HandleAGVActionFinishFeedback();
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class MeasureOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Measure;

        protected override void ActionsWhenOrderCancle()
        {
            base.ActionsWhenOrderCancle();
        }

        protected override void HandleAGVNavigatingFeedback()
        {
            base.HandleAGVNavigatingFeedback();
        }
        protected override void HandleAGVActionStartFeedback()
        {
            base.HandleAGVActionStartFeedback();
            RunningTask.TrafficWaitingState.SetDisplayMessage($"量測任務進行中...");
        }
    }
}

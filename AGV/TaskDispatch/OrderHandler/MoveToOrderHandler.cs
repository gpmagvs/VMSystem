using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class MoveToOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.None;

        protected override void HandleAGVActionFinishFeedback()
        {
            base.HandleAGVActionFinishFeedback();
        }
    }
}

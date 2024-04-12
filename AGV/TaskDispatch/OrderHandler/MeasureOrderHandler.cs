using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class MeasureOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Measure;

        protected override void ActionsWhenOrderCancle()
        {
        }
    }
}

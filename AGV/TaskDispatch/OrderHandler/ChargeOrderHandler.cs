using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class ChargeOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Charge;

        protected override void ActionsWhenOrderCancle()
        {
        }
    }

    public class ExchangeBatteryOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.ExchangeBattery;

        protected override void ActionsWhenOrderCancle()
        {
        }
    }

    public class ParkOrderHandler : ChargeOrderHandler
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Park;
    }

}

using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class MoveToOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.None;

        public override async Task StartOrder(IAGV Agv)
        {
            //Agv.model 
            if (Agv.GetCanNotReachTags().Contains(OrderData.To_Station_Tag))
            {
                _SetOrderAsFaiiureState($"任務終點為[{Agv.model}]車款不可停車的點位", AGVSystemCommonNet6.Alarm.ALARMS.Destine_Point_Is_Not_Allow_To_Reach);
                return;
            }

            await base.StartOrder(Agv);
        }
        protected override void ActionsWhenOrderCancle()
        {
            base.ActionsWhenOrderCancle();
        }

        protected override async Task HandleAGVActionFinishFeedback()
        {
            base.HandleAGVActionFinishFeedback();
        }
    }
}

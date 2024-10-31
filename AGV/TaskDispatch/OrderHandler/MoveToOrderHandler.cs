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
                _SetOrderAsFaiiureState($"任務終點為[{Agv.model}]車款不可停車的點位", AGVSystemCommonNet6.Alarm.ALARMS.Navigation_Path_Contain_Forbidden_Point);
                return;
            }

            await base.StartOrder(Agv);
        }


        protected override async Task HandleAGVActionFinishFeedback()
        {
            base.HandleAGVActionFinishFeedback();
        }
    }
}

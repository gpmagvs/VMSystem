using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class UnloadOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Unload;

        protected override async Task HandleAGVActionFinishFeedback()
        {

            if (RunningTask.ActionType == ACTION_TYPE.Unload)
            {
                await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(RunningTask.DestineTag, RunningTask.ActionType);
            }

            await base.HandleAGVActionFinishFeedback();
        }
        public override Task StartOrder(IAGV Agv)
        {
            Task<clsAGVSTaskReportResponse> response = AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.To_Station_Tag, ACTION_TYPE.Unload);
            response.Wait();
            return base.StartOrder(Agv);
        }

        protected override void ActionsWhenOrderCancle()
        {
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.To_Station_Tag, ACTION_TYPE.Load);

        }
    }
}

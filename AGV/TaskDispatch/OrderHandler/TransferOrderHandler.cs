using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.VMS;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class TransferOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Carry;

        protected override async Task HandleAGVActionFinishFeedback()
        {

            if (RunningTask.ActionType == ACTION_TYPE.Load || RunningTask.ActionType == ACTION_TYPE.Unload)
            {
                await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(RunningTask.DestineTag, RunningTask.ActionType);

                if (RunningTask.ActionType == ACTION_TYPE.Unload)
                {
                    await AGVSSerivces.TRANSFER_TASK.StartTransferCargoReport(this.Agv.Name, OrderData.From_Station_Tag, OrderData.To_Station_Tag);
                }

            }

            await base.HandleAGVActionFinishFeedback();
        }
    }


}

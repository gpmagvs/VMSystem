using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using static SQLite.SQLite3;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using RosSharp.RosBridgeClient.MessageTypes.Geometry;

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
        public override async Task StartOrder(IAGV Agv)
        {
            clsAGVSTaskReportResponse result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.From_Station_Tag, Convert.ToInt16(OrderData.From_Slot), OrderData.To_Station_Tag, Convert.ToInt16(OrderData.To_Slot), ACTION_TYPE.Load);
            if (result.confirm)
            {
                await base.StartOrder(Agv);
            }
            else
            {
                _SetOrderAsFaiiureState("LoadOrder Start Fail" + result.message);
            }
        }

        protected override void ActionsWhenOrderCancle()
        {
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.To_Station_Tag, ACTION_TYPE.Load);

        }

    }
}

using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using static SQLite.SQLite3;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class UnloadOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Unload;

        protected override async Task HandleAGVActionFinishFeedback()
        {
            await base.HandleAGVActionFinishFeedback();
        }
        public override async Task StartOrder(IAGV Agv)
        {
            if (Agv.IsAGVHasCargoOrHasCargoID())
            {
                _SetOrderAsFaiiureState("AGV車上有貨物或有帳籍資料時不可執行取貨任務", ALARMS.CANNOT_DISPATCH_UNLOAD_TASK_WHEN_AGV_HAS_CARGO);
                return;
            }
            clsAGVSTaskReportResponse result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.From_Station_Tag, Convert.ToInt16(OrderData.From_Slot), OrderData.To_Station_Tag, Convert.ToInt16(OrderData.To_Slot), ACTION_TYPE.Unload);
            if (result.confirm)
            {
                await base.StartOrder(Agv);
            }
            else
            {
                _SetOrderAsFaiiureState("UnloadOrder Start Fail, Reason:" + result.message, alarm: result.AlarmCode);
            }
        }

        protected override void ActionsWhenOrderCancle()
        {
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.To_Station_Tag, ACTION_TYPE.Load, Agv.Name);

        }
    }
}

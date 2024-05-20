using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using AGVSystemCommonNet6.Microservices.VMS;
using System.Diagnostics.CodeAnalysis;

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
        public override async Task StartOrder(IAGV Agv)
        {
            // TODO 把可用的轉換站存在這listTransferStation
            var loadAtTransferStationTask = SequenceTaskQueue.Where(x => x.GetType() == typeof(VMSystem.AGV.TaskDispatch.Tasks.LoadAtTransferStationTask)).FirstOrDefault();
            int destineTag = OrderData.need_change_agv /*&& RunningTask.TransferStage == TransferStage.MoveToTransferStationLoad*/ ? OrderData.TransferToTag : OrderData.To_Station_Tag;
            clsAGVSTaskReportResponse result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.From_Station_Tag, destineTag, ACTION_TYPE.Carry);
            if (result.confirm)
                await base.StartOrder(Agv);
            else
            {
                this.Agv = Agv;
                _SetOrderAsFaiiureState(result.message);
            }
        }


        protected override void ActionsWhenOrderCancle()
        {
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.To_Station_Tag, ACTION_TYPE.Load);
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.From_Station_Tag, ACTION_TYPE.Unload);
        }
    }


}

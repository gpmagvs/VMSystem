﻿using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
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
        public override Task StartOrder(IAGV Agv)
        {
            int destineTag = OrderData.need_change_agv ? OrderData.ChangeAGVMiddleStationTag : OrderData.To_Station_Tag;
            AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.From_Station_Tag, destineTag, ACTION_TYPE.Carry);
            return base.StartOrder(Agv);
        }


        protected override void ActionsWhenOrderCancle()
        {
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.To_Station_Tag, ACTION_TYPE.Load);
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.From_Station_Tag, ACTION_TYPE.Unload);
        }
    }


}

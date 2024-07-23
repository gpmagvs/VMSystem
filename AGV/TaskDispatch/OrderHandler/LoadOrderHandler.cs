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
            await base.HandleAGVActionFinishFeedback();
        }
        public override async Task StartOrder(IAGV Agv)
        {
            if (!Agv.IsAGVHasCargoOrHasCargoID())
            {
                _SetOrderAsFaiiureState("AGV車上沒有貨物或有帳籍資料時不可執行放貨任務", ALARMS.CANNOT_DISPATCH_UNLOAD_TASK_WHEN_AGV_HAS_CARGO);
                return;
            }
            if (OrderData.need_change_agv == true) // 參考 TransferOrderHandler.StartOrder 尋找可用轉運站
            {
                // TODO 把可用的轉換站存在這listTransferStation
                var loadAtTransferStationTask = SequenceTaskQueue.Where(x => x.GetType() == typeof(VMSystem.AGV.TaskDispatch.Tasks.LoadAtTransferStationTask)).FirstOrDefault();
                if (loadAtTransferStationTask == null)
                {
                    //TODO error
                }
                else
                {
                    VMSystem.AGV.TaskDispatch.Tasks.LoadAtTransferStationTask task = (VMSystem.AGV.TaskDispatch.Tasks.LoadAtTransferStationTask)loadAtTransferStationTask;
                    bool IsAllTransferStationFail = true;
                    string strFailMsg = "";
                    if (task.dict_Transfer_to_from_tags == null)
                    {
                        _SetOrderAsFaiiureState("LoadOrder Start Fail, Reason: dict_Transfer_to_from_tags not foound", ALARMS.Transfer_Tags_Not_Found);
                        return;
                    }
                    // 檢查可用轉運站狀態
                    foreach (var tag in task.dict_Transfer_to_from_tags)
                    {
                        int intTransferToTag = tag.Key;
                        clsAGVSTaskReportResponse result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.From_Station_Tag, Convert.ToInt16(OrderData.From_Slot), intTransferToTag, 0, ACTION_TYPE.Load);
                        if (result.confirm)
                        {
                            OrderData.TransferToTag = intTransferToTag;
                            OrderData.TransferFromTag = tag.Value.FirstOrDefault();
                            OrderData.To_Slot = "0"; // 轉運站會在第0層
                            await base.StartOrder(Agv);
                            IsAllTransferStationFail = false;
                            break;
                        }
                        else
                        {
                            strFailMsg += $"Tag-{intTransferToTag}:{result.message},";
                        }
                    }
                    if (IsAllTransferStationFail)
                    {
                        this.Agv = Agv;
                        _SetOrderAsFaiiureState("all transfer station fail:" + strFailMsg, ALARMS.No_Transfer_Station_To_Work);
                    }
                }
            }
            else
            {
                clsAGVSTaskReportResponse result = new clsAGVSTaskReportResponse() { confirm = false, message = "[LoadOrderHandler.StartOrder] error" };
                if (OrderData.bypass_eq_status_check == true)
                {
                    result.confirm = true;
                    result.message = "bypass_eq_status_check";
                }
                else
                    result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData.From_Station_Tag, Convert.ToInt16(OrderData.From_Slot), OrderData.To_Station_Tag, Convert.ToInt16(OrderData.To_Slot), ACTION_TYPE.Load);
                if (result.confirm)
                {
                    await base.StartOrder(Agv);
                }
                else
                {
                    _SetOrderAsFaiiureState("LoadOrder Start Fail, Reason:" + result.message, result.AlarmCode);
                }
            }
        }

        protected override void ActionsWhenOrderCancle()
        {
            AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.To_Station_Tag, ACTION_TYPE.Load, Agv.Name);

        }

    }
}

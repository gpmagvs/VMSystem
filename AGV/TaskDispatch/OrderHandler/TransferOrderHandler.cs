using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using AGVSystemCommonNet6.Microservices.VMS;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using VMSystem.TrafficControl;
using static SQLite.SQLite3;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class TransferOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Carry;

        protected override async Task HandleAGVActionFinishFeedback()
        {
            await base.HandleAGVActionFinishFeedback();
        }
        public override async Task StartOrder(IAGV Agv)
        {
            if (Agv.IsAGVHasCargoOrHasCargoID() && this.OrderData.From_Station != Agv.Name)
            {
                _SetOrderAsFaiiureState("AGV車上有貨物或有帳籍資料,且來源非AGV時不可執行搬運任務", ALARMS.CANNOT_DISPATCH_CARRY_TASK_WHEN_AGV_HAS_CARGO);
                return;
            }

            (bool confirm, string message) v = await AGVSSerivces.TaskReporter((OrderData, MCSCIMService.TaskStatus.start));
            if (v.confirm == false)
                LOG.WARN($"{v.message}");
            if (OrderData.need_change_agv)
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
                        clsAGVSTaskReportResponse result = new clsAGVSTaskReportResponse() { confirm = false, message = "[TransferOrderHandler.StartOrder] error" };
                        if (OrderData.bypass_eq_status_check == true)
                        {
                            result.confirm = true;
                            result.message = "bypass_eq_status_check";
                        }
                        else
                            result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData, ACTION_TYPE.Carry, isSourceAGV: OrderData.IsFromAGV);
                        if (result.confirm)
                        {
                            OrderData.TransferToTag = intTransferToTag;
                            OrderData.TransferFromTag = tag.Value.FirstOrDefault();
                            await base.StartOrder(Agv);
                            IsAllTransferStationFail = false;
                            break;
                        }
                        else
                        {
                            strFailMsg += $"{result.message},";
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
                if (OrderData.transfer_task_stage == 2)
                {
                    OrderData.From_Slot = "0";
                    if (OrderData.To_Slot == "-1")
                        OrderData.To_Slot = "-2";
                }
                clsAGVSTaskReportResponse result = new clsAGVSTaskReportResponse() { confirm = false, message = "[TransferOrderHandler.StartOrder] error" };
                if (OrderData.bypass_eq_status_check == true)
                {
                    result.confirm = true;
                    result.message = "bypass_eq_status_check";
                }
                else
                {
                    result = await AGVSSerivces.TRANSFER_TASK.StartLDULDOrderReport(OrderData, ACTION_TYPE.Carry, isSourceAGV: OrderData.IsFromAGV);
                }
                if (result.confirm)
                {
                    if (result.ReturnObj != null)
                    {
                        OrderData.To_Slot = result.ReturnObj.ToString();
                    }
                    await base.StartOrder(Agv);
                }
                else
                {
                    this.Agv = Agv;
                    _SetOrderAsFaiiureState(result.message, result.AlarmCode);
                }
            }
        }
        protected override async Task ActionsWhenOrderCancle()
        {
            try
            {
                clsAGVSTaskReportResponse response1 = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.TaskName, OrderData.To_Station_Tag, ACTION_TYPE.Load, Agv.Name);
                logger.Info(response1.ToJson());
                clsAGVSTaskReportResponse response2 = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.TaskName, OrderData.From_Station_Tag, ACTION_TYPE.Unload, Agv.Name);
                logger.Info(response2.ToJson());

            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }


}

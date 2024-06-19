using AGVSystemCommonNet6.AGVDispatch.Messages;
using System.Diagnostics;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static SQLite.SQLite3;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class ChargeOrderHandler : OrderHandlerBase
    {
        public event EventHandler<ChargeOrderHandler> onAGVChargeOrderDone;
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Charge;
        protected override void _SetOrderAsFinishState()
        {
            base._SetOrderAsFinishState();
            onAGVChargeOrderDone?.Invoke(this, this);
        }
        public override async Task StartOrder(IAGV Agv)
        {

            if (!IsChargeStationUsableCheck(Agv))
            {
                _SetOrderAsFaiiureState("ChargeOrder Start Fail, Reason:  Destine Charge Station Can't Use", AGVSystemCommonNet6.Alarm.ALARMS.Destine_Charge_Station_Has_AGV);
                return;
            }

            if (Agv.model != AGV_TYPE.SUBMERGED_SHIELD)
            {
                if (Agv.states.Cargo_Status != 0)
                {
                    _SetOrderAsFaiiureState("ChargeOrder Start Fail, Reason: Cargo_Status!=0", AGVSystemCommonNet6.Alarm.ALARMS.CANNOT_DISPATCH_CHARGE_TASK_WHEN_AGV_HAS_CARGO);
                    return;
                }
                if (Agv.states.CSTID != null && Agv.states.CSTID.Any(str => str != ""))
                {
                    _SetOrderAsFaiiureState("ChargeOrder Start Fail, Reason: CSTID not empty", AGVSystemCommonNet6.Alarm.ALARMS.CANNOT_DISPATCH_CHARGE_TASK_WHEN_AGV_HAS_CARGO);
                    return;
                }
            }
            await base.StartOrder(Agv);
        }

        private bool IsChargeStationUsableCheck(IAGV Agv)
        {
            int chargeStationTag = OrderData.To_Station_Tag;
            var otherAGVList = VMSManager.AllAGV.FilterOutAGVFromCollection(Agv);
            bool _isAnyVehicleGoToStation = otherAGVList.Any(agv => agv.CurrentRunningTask().OrderData?.To_Station_Tag == chargeStationTag);
            bool _isAnyVehicleAtStation = otherAGVList.Any(agv=>agv.currentMapPoint.TagNumber == chargeStationTag);
            return  ! _isAnyVehicleGoToStation  && !_isAnyVehicleAtStation;
        }
    }

    public class ExchangeBatteryOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.ExchangeBattery;

        protected override void ActionsWhenOrderCancle()
        {

        }
        protected override void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            base.HandleAGVNavigatingFeedback(feedbackData);
        }
        protected override void HandleAGVActionStartFeedback()
        {
            base.HandleAGVActionStartFeedback();
            RunningTask.TrafficWaitingState.SetDisplayMessage("電池交換中...");
        }
    }

    public class ParkOrderHandler : ChargeOrderHandler
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.Park;
    }

}

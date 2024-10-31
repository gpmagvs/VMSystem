using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using System.Diagnostics;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
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

            if (Agv.model != AGV_TYPE.SUBMERGED_SHIELD)
            {
                if (Agv.states.Cargo_Status != 0)
                {
                    _SetOrderAsFaiiureState("ChargeOrder Start Fail, Reason: Cargo_Status!=0", AGVSystemCommonNet6.Alarm.ALARMS.CannotAssignChargeJobBecauseWrongCargoStatus);
                    return;
                }
                if (Agv.states.CSTID != null && Agv.states.CSTID.Any(str => str != ""))
                {
                    _SetOrderAsFaiiureState("ChargeOrder Start Fail, Reason: CSTID not empty", AGVSystemCommonNet6.Alarm.ALARMS.CannotAssignChargeJobBecauseWrongCargoStatus);
                    return;
                }
            }
            if (!IsChargeStationUsableCheck(Agv, out string errMsg))
            {
                AlarmManagerCenter.AddAlarmAsync(ALARMS.Destine_Charge_Station_Has_AGV, Equipment_Name: Agv.Name, location: OrderData.To_Station, taskName: OrderData.TaskName, level: ALARM_LEVEL.WARNING);
                int oriTag = OrderData.To_Station_Tag;
                OrderData.To_Station = "-1";
                (bool confirm, ALARMS alarm_code) = await Agv.taskDispatchModule.CheckTaskOrderContentAndTryFindBestWorkStation(OrderData);
                if (confirm)
                {
                    logger.Trace($"Try change charge station to go (From Tag {oriTag}->{OrderData.To_Station_Tag})");
                }
                else
                {
                    _SetOrderAsFaiiureState(errMsg, AGVSystemCommonNet6.Alarm.ALARMS.Destine_Charge_Station_Has_AGV);
                    return;
                }

                //return;
            }
            await base.StartOrder(Agv);
        }

        private bool IsChargeStationUsableCheck(IAGV Agv, out string message)
        {
            message = "";
            int chargeStationTag = OrderData.To_Station_Tag;
            MapPoint _mapPoint = StaMap.GetPointByTagNumber(chargeStationTag);

            var otherAGVList = VMSManager.AllAGV.FilterOutAGVFromCollection(Agv);
            List<IAGV> gotoSameStationVehicles = otherAGVList.Where(agv => agv.CurrentRunningTask().OrderData?.To_Station_Tag == chargeStationTag).ToList();
            bool _isAnyVehicleGoToStation = gotoSameStationVehicles.Any();
            List<IAGV> alreadyAtSameChargeStationVehicles = otherAGVList.Where(agv => agv.currentMapPoint.TagNumber == chargeStationTag).ToList();
            bool _isAnyVehicleAtStation = alreadyAtSameChargeStationVehicles.Any();

            if (_isAnyVehicleGoToStation)
            {
                message = $"{gotoSameStationVehicles.GetNames()} already has task go to charge station [{_mapPoint.Graph.Display}]";
                logger.Warn(message);
            }
            if (_isAnyVehicleAtStation)
            {
                message = $"{alreadyAtSameChargeStationVehicles.GetNames()} already at charge station [{_mapPoint.Graph.Display}]";
                logger.Warn(message);
            }
            return !_isAnyVehicleGoToStation && !_isAnyVehicleAtStation;
        }
    }

    public class ExchangeBatteryOrderHandler : OrderHandlerBase
    {
        public override ACTION_TYPE OrderAction => ACTION_TYPE.ExchangeBattery;

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

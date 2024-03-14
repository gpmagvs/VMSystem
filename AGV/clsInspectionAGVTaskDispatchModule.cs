using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV
{
    public class clsInspectionAGVTaskDispatchModule : clsAGVTaskDisaptchModule
    {
        public clsInspectionAGVTaskDispatchModule(IAGV agv) : base(agv)
        {
        }

        protected override bool SearchDestineStation(ACTION_TYPE action, out MapPoint optimized_workstation, out ALARMS alarm_code)
        {
            try
            {
                LOG.INFO($"Search 電池交換站-{action}");
                optimized_workstation = null;
                alarm_code = ALARMS.NONE;
                if (action != ACTION_TYPE.ExchangeBattery)
                {
                    alarm_code = ALARMS.CANT_AUTO_SEARCH_STATION_TYPE_IS_NOT_EXCHANGE_FOR_INSPECTION_AGV;
                    return false;
                }

                List<MapPoint> exchangersPoints = StaMap.GetChargeableStations();
                optimized_workstation = exchangersPoints.Count == 0 ? null : exchangersPoints.First();
                alarm_code = optimized_workstation == null ? ALARMS.NO_AVAILABLE_BAT_EXCHANGER_USABLE : ALARMS.NONE;
                return optimized_workstation != null;

            }
            catch (Exception ex)
            {
                throw ex;
            }

        }
    }
}

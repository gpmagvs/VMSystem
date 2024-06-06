using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;

namespace VMSystem.AGV
{
    public class clsInspectionAGVTaskDispatchModule : clsAGVTaskDisaptchModule
    {
        public clsInspectionAGVTaskDispatchModule(IAGV agv) : base(agv)
        {
        }
        protected override async Task<(bool confirm, MapPoint optimized_workstation, ALARMS alarm_code)> SearchDestineStation(ACTION_TYPE action)
        {
            try
            {
                LOG.INFO($"Search 電池交換站-{action}");
                MapPoint optimized_workstation = null;
                ALARMS alarm_code = ALARMS.NONE;
                if (action != ACTION_TYPE.ExchangeBattery)
                {
                    alarm_code = ALARMS.CANT_AUTO_SEARCH_STATION_TYPE_IS_NOT_EXCHANGE_FOR_INSPECTION_AGV;
                    return (false, null, alarm_code);
                }

                List<MapPoint> exchangersPoints = StaMap.GetChargeableStations();
                optimized_workstation = exchangersPoints.Count == 0 ? null : exchangersPoints.First();
                alarm_code = optimized_workstation == null ? ALARMS.NO_AVAILABLE_BAT_EXCHANGER_USABLE : ALARMS.NONE;
                return (optimized_workstation != null, optimized_workstation, alarm_code);

            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        protected override async Task CheckAutoCharge()
        {
            return;
        }
    }
}

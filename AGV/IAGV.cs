using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using static AGVSystemCommonNet6.clsEnums;
using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV
{
    public interface IAGV
    {
        AvailabilityHelper availabilityHelper { get; }
        AGV_MODEL model { get; set; }
        string Name { get; set; }
        clsConnections connections { get; set; }
        bool connected { get; }
        ONLINE_STATE online_state { get; set; }
        MAIN_STATUS main_state { get; }
        RunningStatus states { get; set; }
        IAGVTaskDispather taskDispatchModule { get; set; }
        Map map { get; set; }
        bool simulationMode { get; set; }

        bool Online(out string message);
        bool Offline(out string message);

        /// <summary>
        /// 
        /// </summary>
        List<int> NavigatingTagPath { get; }

        Task<object> GetAGVState();

        int CalculatePathCost(Map map, object toTag);
        AGVStatusDBHelper AGVStatusDBHelper { get; }
        string AddNewAlarm(ALARMS alarm_enum, ALARM_SOURCE source = ALARM_SOURCE.EQP, ALARM_LEVEL Level = ALARM_LEVEL.WARNING);
    }

}

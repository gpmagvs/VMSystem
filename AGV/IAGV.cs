using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using static AGVSystemCommonNet6.clsEnums;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;

namespace VMSystem.AGV
{
    public interface IAGV
    {
        AvailabilityHelper availabilityHelper { get; }
        VMS_GROUP VMSGroup { get; set; }
        AGV_MODEL model { get; set; }
        string Name { get; set; }
        clsAGVOptions options { get; set; }
        bool connected { get; set; }
        ONLINE_STATE online_state { get; set; }
        MAIN_STATUS main_state { get; }
        RunningStatus states { get; set; }
        IAGVTaskDispather taskDispatchModule { get; set; }
        Map map { get; set; }
        MapPoint currentMapPoint { get; }
        clsMapPoint[] RemainTrajectory { get; }
        bool Online(out string message);
        bool Offline(out string message);

        /// <summary>
        /// 
        /// </summary>
        List<int> NavigatingTagPath { get; }

        Task<object> GetAGVStateFromDB();

        Task<bool> SaveStateToDatabase(clsAGVStateDto dto);
        int CalculatePathCost(Map map, object toTag);
        AGVStatusDBHelper AGVStatusDBHelper { get; }
        string AddNewAlarm(ALARMS alarm_enum, ALARM_SOURCE source = ALARM_SOURCE.EQP, ALARM_LEVEL Level = ALARM_LEVEL.WARNING);
         void UpdateAGVStates(RunningStatus status);
        Task PublishTrafficDynamicData(clsDynamicTrafficState dynamicTrafficState);
    }

}

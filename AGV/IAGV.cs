using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.clsEnums;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.DATABASE.Helpers;
using VMSystem.AGV.TaskDispatch;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.Microservices.VMS;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP.Geometry;
using static VMSystem.AGV.clsGPMInspectionAGV;

namespace VMSystem.AGV
{
    public interface IAGV
    {
        Task Run();
        AvailabilityHelper availabilityHelper { get; }
        VMS_GROUP VMSGroup { get; set; }
        AGV_TYPE model { get; set; }
        string Name { get; set; }
        clsAGVOptions options { get; set; }
        HttpHelper AGVHttp { get; set; }
        bool connected { get; set; }

        ONLINE_STATE online_mode_req { get; set; }
        ONLINE_STATE online_state { get; set; }
        MAIN_STATUS main_state { get; set; }
        clsRunningStatus states { get; set; }
        IAGVTaskDispather taskDispatchModule { get; set; }
        Map map { get; set; }
        MapPoint currentMapPoint { get; set; }
        bool AGVOnlineFromAGV(out string message);
        bool AGVOfflineFromAGV(out string message);

        bool AGVOnlineFromAGVS(out string message);
        bool AGVOfflineFromAGVS(out string message);

        MapPoint[] PlanningNavigationMapPoints { get; }
        Task<object> GetAGVStateFromDB();

        int CalculatePathCost(Map map, object toTag);
        AGVStatusDBHelper AGVStatusDBHelper { get; }
        bool IsTrafficTaskExecuting { get; set; }
        bool IsTrafficTaskFinish { get; set; }

        string AddNewAlarm(ALARMS alarm_enum, ALARM_SOURCE source = ALARM_SOURCE.EQP, ALARM_LEVEL Level = ALARM_LEVEL.WARNING);
        Task PublishTrafficDynamicData(clsDynamicTrafficState dynamicTrafficState);

        void CheckAGVStatesBeforeDispatchTask(ACTION_TYPE action, MapPoint DestinePoint);

        clsAGVSimulation AgvSimulation { get; set; }
        clsAGVSTcpServer.clsAGVSTcpClientHandler? TcpClientHandler { get; set; }
        bool IsSolvingTrafficInterLock { get; set; }

        /// <summary>
        /// AGV是否在充電站內閒置且電量低於閥值
        /// </summary>
        /// <returns></returns>
        bool IsAGVIdlingAtChargeStationButBatteryLevelLow();

        bool IsAGVIdlingAtNormalPoint();

        bool IsAGVCargoStatusCanNotGoToCharge();
        Task<(bool confirm, string message)> Locating(clsLocalizationVM localizationVM);

        MapRectangle AGVGeometery { get; }
        MapCircleArea AGVRotaionGeometry { get; }
        int currentFloor { get; set; }

    }

}

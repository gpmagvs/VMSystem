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
using VMSystem.TrafficControl;

namespace VMSystem.AGV
{
    public interface IAGV
    {
        public enum BATTERY_STATUS
        {
            UNKNOWN,
            DEEPCHARGING,
            LOW,
            MIDDLE_LOW,
            MIDDLE_HIGH,
            HIGH,
        }
        Task Run();
        bool IsStatusSyncFromThirdPartySystem { get; set; }

        NLog.Logger logger { get; set; }
        AvailabilityHelper availabilityHelper { get; }
        VMS_GROUP VMSGroup { get; set; }
        AGV_TYPE model { get; set; }
        string Name { get; set; }
        int AgvID { get; set; }
        clsAGVOptions options { get; set; }
        HttpHelper AGVHttp { get; set; }
        bool connected { get; set; }
        BATTERY_STATUS batteryStatus { get; }

        ONLINE_STATE online_mode_req { get; set; }
        ONLINE_STATE online_state { get; set; }
        MAIN_STATUS main_state { get; set; }
        clsRunningStatus states { get; set; }

        VehicleNavigationState NavigationState { get; set; }
        IAGVTaskDispather taskDispatchModule { get; set; }
        Map map { get; set; }
        MapPoint currentMapPoint { get; set; }
        bool AGVOnlineFromAGV(out string message);
        bool AGVOfflineFromAGV(out string message);

        bool AGVOnlineFromAGVS(out string message);
        bool AGVOfflineFromAGVS(out string message);

        List<MapPoint> noRegistedByConflicCheck { get; set; }
        List<MapPoint> RegistedByConflicCheck { get; set; }
        MapPoint[] PlanningNavigationMapPoints { get; }

        int CalculatePathCost(Map map, object toTag);
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
        bool IsAGVIdlingAtParkableStationButBatteryLevelLow();

        bool IsAGVIdlingAtNormalPoint();

        bool IsAGVHasCargoOrHasCargoID();
        Task<(bool confirm, string message)> Locating(clsLocalizationVM localizationVM);
        Task<bool> SpeedRecovertRequest();
        Task<bool> SpeedSlowRequest();

        bool CheckOutOrderExecutableByBatteryStatusAndChargingStatus(ACTION_TYPE orderAction, out string message);
        MapRectangle AGVRealTimeGeometery { get; }
        MapRectangle AGVCurrentPointGeometery { get; }
        MapCircleArea AGVRotaionGeometry { get; }
        int currentFloor { get; set; }

        bool IsDirectionHorizontalTo(IAGV OtherAGV);
        Task CancelTaskAsync(string task_name, string reason);
        void StartDeepCharging();
        void StopDeepCharge();

        /// <summary>
        /// AGV當前是否需要深充(通常是從車載狀態決定 ex.上報了異常碼)
        /// </summary>
        /// <returns></returns>
        bool IsDeepChargeRequired();

        event EventHandler<string> OnTaskCancel;
        event EventHandler<int> OnMapPointChanged;
        event EventHandler OnAGVStatusDown;

        TaskExecuteHelper TaskExecuter { get; set; }
    }

}

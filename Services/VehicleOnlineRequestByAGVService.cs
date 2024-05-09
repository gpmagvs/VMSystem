using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.Services
{

    public abstract class VehicleOnlineBase
    {
        public virtual (ALARMS alarmCode, string message) OnlineRequest(string VehicleName, out IAGV _vehicle)
        {
            _vehicle = GetVehicle(VehicleName);
            if (_vehicle == null)
                return (ALARMS.GET_ONLINE_REQ_BUT_AGV_IS_NOT_REGISTED, $"車輛-{VehicleName} 未被系統註冊");
            var MainStatusCheckResult = CheckMainStatus(_vehicle);

            if (MainStatusCheckResult.alarmCode != ALARMS.NONE)
                return MainStatusCheckResult;

            var LocationCheckResult = CheckLocation(_vehicle);
            if (LocationCheckResult.alarmCode != ALARMS.NONE)
                return LocationCheckResult;

            return (ALARMS.NONE, "");
        }
        public virtual (ALARMS alarmCode, string message) OfflineRequest(string VehicleName, out IAGV _vehicle)
        {
            _vehicle = GetVehicle(VehicleName);
            return (ALARMS.NONE, "");

        }

        private IAGV GetVehicle(string VehicleName)
        {
            return VMSManager.GetAGVByName(VehicleName);
        }
        private (ALARMS alarmCode, string message) CheckLocation(IAGV _vehicle)
        {
            int _currentTagReport = _vehicle.states.Last_Visited_Node;

            if (!StaMap.TryGetPointByTagNumber(_currentTagReport, out MapPoint point))
            {
                return (ALARMS.GET_ONLINE_REQ_BUT_AGV_LOCATION_IS_NOT_EXIST_ON_MAP, $"車輛上報點位不存在於當前地圖");
            }
            else
            {
                clsCoordination currentCoord = _vehicle.states.Coordination;
                if (point.CalculateDistance(currentCoord.X, currentCoord.Y) > 1.5)
                    return (ALARMS.GET_ONLINE_REQ_BUT_AGV_LOCATION_IS_TOO_FAR_FROM_POINT, "車輛當前位置距離上報點位過遠");
                return (ALARMS.NONE, "");
            }
        }

        private (ALARMS alarmCode, string message) CheckMainStatus(IAGV _vehicle)
        {
            bool _isVehicleDown = _vehicle.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.IDLE && _vehicle.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.Charging;
            if (_isVehicleDown)
            {

                return (ALARMS.GET_ONLINE_REQ_BUT_AGV_STATE_ERROR, $"車輛-{_vehicle.Name} 當前狀態不可上線({_vehicle.main_state})");
            }
            return (ALARMS.NONE, "");

        }

    }

    public class VehicleOnlineBySystemService : VehicleOnlineBase
    {
        public override (ALARMS alarmCode, string message) OnlineRequest(string VehicleName, out IAGV Vehicle)
        {
            LOG.WARN($"User 要求 {VehicleName} 上線");

            var results = base.OnlineRequest(VehicleName, out Vehicle);
            if (results.alarmCode == ALARMS.NONE)
            {
                if (!Vehicle.AGVOnlineFromAGVS(out string msg))
                    results.message = msg;
            }

            return results;
        }
    }

    public class VehicleOnlineRequestByAGVService : VehicleOnlineBase
    {
        public override (ALARMS alarmCode, string message) OnlineRequest(string VehicleName, out IAGV Vehicle)
        {
            LOG.WARN($"車輛-{VehicleName} 請求上線");
            return base.OnlineRequest(VehicleName, out Vehicle);
        }
    }
}

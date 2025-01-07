using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.Dispatch.Configurations;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.Dispatch
{
    /// <summary>
    /// 負責選出指定車輛(嘗試)
    /// </summary>
    public class SpecificVehicleAssigner
    {
        EQStationDedicatedConfiguration _dedicatedConfiguration;
        public SpecificVehicleAssigner(EQStationDedicatedConfiguration eQStationDedicatedConfiguration)
        {
            _dedicatedConfiguration = eQStationDedicatedConfiguration;
        }

        internal bool TryGetSpeficVehicle(clsTaskDto taskDto, out IAGV speficVehicle)
        {
            speficVehicle = null;

            ACTION_TYPE _orderAction = taskDto.Action;

            if (_orderAction != ACTION_TYPE.Carry && _orderAction != ACTION_TYPE.Unload)
                return false;
            if (!string.IsNullOrEmpty(taskDto.DesignatedAGVName))
                return false;

            int _fromStationTag = taskDto.From_Station_Tag;

            EQStationDedicatedSetting speficVehicleSetting = _dedicatedConfiguration.EQStationDedicatedSettings.Find(item => item.tag == _fromStationTag);

            if (speficVehicleSetting == null || !speficVehicleSetting.dedicatedStations.Any())
                return false;

            bool anyVehicleFound = TryGetVehiclesAtSpeficStations(speficVehicleSetting.dedicatedStations, out List<IAGV> VehiclesAtSpeficStations);

            if (!anyVehicleFound)
                return false;

            //filter out offline、 down、 battery level too low 、 executing order vehilces

            VehiclesAtSpeficStations = VehiclesAtSpeficStations.Where(v => v.online_state == clsEnums.ONLINE_STATE.ONLINE &&
                                                                            v.main_state != clsEnums.MAIN_STATUS.DOWN &&
                                                                            v.batteryStatus != IAGV.BATTERY_STATUS.LOW && v.batteryStatus != IAGV.BATTERY_STATUS.DEEPCHARGING &&
                                                                            v.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                                                                            .ToList();
            if (!VehiclesAtSpeficStations.Any())
                return false;
            //filter out vehilces by station accept model. 

            clsEnums.AGV_TYPE fromStationAcceptVehicleModel = Equipment.EquipmentStore.GetEQAcceptAGVType(_fromStationTag, taskDto.GetFromSlotInt());

            if (fromStationAcceptVehicleModel != clsEnums.AGV_TYPE.Any)
            {
                VehiclesAtSpeficStations = VehiclesAtSpeficStations.Where(vehicle => vehicle.model == fromStationAcceptVehicleModel).ToList();
            }

            //sort by distance to source station

            Dictionary<IAGV, double> _distanceMap = VehiclesAtSpeficStations.ToDictionary(vehicle => vehicle, vehicle => CalculateMoveDistanceToSource(vehicle, _fromStationTag));
            _distanceMap = _distanceMap.OrderBy(pair => pair.Value).ToDictionary(p => p.Key, p => p.Value);
            speficVehicle = _distanceMap.First().Key;
            return speficVehicle != null;
        }

        private double CalculateMoveDistanceToSource(IAGV vehicle, int sourceStationTag)
        {

            PathFinder _pathFinder = new PathFinder();
            clsPathInfo _pathInfo = _pathFinder.FindShortestPathByTagNumber(StaMap.Map, vehicle.currentMapPoint.TagNumber, sourceStationTag, new PathFinder.PathFinderOption()
            {
                Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
                OnlyNormalPoint = false,
                Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
            });
            if (_pathInfo == null || !_pathInfo.stations.Any())
                return double.MaxValue;

            return _pathInfo.total_travel_distance;
        }

        private bool TryGetVehiclesAtSpeficStations(List<int> dedicatedStations, out List<IAGV> vehilces)
        {
            vehilces = new List<IAGV>();

            //Get All Vehicle At dedicatedStations; 
            vehilces = VMSManager.AllAGV.Where(_vehicle => dedicatedStations.Contains(_vehicle.currentMapPoint.TagNumber))
                                        .ToList();
            return vehilces.Any();
        }
    }
}

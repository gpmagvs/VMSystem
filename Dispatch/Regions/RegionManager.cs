using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;

namespace VMSystem.Dispatch.Regions
{
    public class RegionManager
    {
        public static IEnumerable<MapRegion> GetRegions()
        {
            return StaMap.Map.Regions;
        }

        internal static void RegistRegionToGo(IAGV vehicle, MapPoint finalMapPoint)
        {
            var agvReservedRegions = GetRegions().Where(region => region.ReserveRegionVehicles.Contains(vehicle.Name));
            foreach (var _region in agvReservedRegions)
            {
                _region.ReserveRegionVehicles.Remove(vehicle.Name);
            }
            var regionToGo = finalMapPoint.GetRegion();
            regionToGo.ReserveRegionVehicles.Add(vehicle.Name);
        }



        internal static void UpdateRegion(IAGV vehicle)
        {
            var agvPreviousRegions = GetRegions().Where(region => region.InRegionVehicles.Contains(vehicle.Name));
            foreach (var _region in agvPreviousRegions)
                _region.InRegionVehicles.Remove(vehicle.Name);
            vehicle.currentMapPoint.GetRegion().InRegionVehicles.Add(vehicle.Name);
        }

        internal static bool TryGetRegionMaxCapcity(MapPoint goalPoint, out int MaxVehicleCapacity)
        {
            MaxVehicleCapacity = goalPoint.GetRegion().MaxVehicleCapacity;
            return MaxVehicleCapacity != -1;
        }

        internal static bool TryGetRegionEntryPoints(MapPoint goalPoint, out IEnumerable<MapPoint> entryPoints)
        {
            entryPoints = goalPoint.GetRegion().EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag));
            return entryPoints.Any();
        }

        internal static bool TryGetRegionLeavePoints(MapPoint goalPoint, out IEnumerable<MapPoint> leavePoints)
        {
            leavePoints = goalPoint.GetRegion().LeavingTags.Select(tag => StaMap.GetPointByTagNumber(tag));
            return leavePoints.Any();
        }
        internal static IEnumerable<MapPoint> GetRegionEntryPoints(MapPoint goalPoint)
        {
            return GetRegionEntryPoints(goalPoint.GetRegion());
        }
        internal static IEnumerable<MapPoint> GetRegionEntryPoints(MapRegion nextRegion)
        {
            return nextRegion.EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag));

        }

        internal static bool IsRegionEnterable(IAGV WannaEntryRegionVehicle, MapRegion regionQuery, out List<string> inRegionVehicles)
        {
            inRegionVehicles = new List<string>();
            // 取得當前區域
            MapRegion _Region = StaMap.Map.Regions.FirstOrDefault(reg => reg.Name == regionQuery.Name);
            if (_Region == null)
                return true;

            List<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(WannaEntryRegionVehicle).ToList();

            try
            {
                //搜尋其他會通過會停在該區域的車輛
                List<IAGV> goToRegionVehicles = otherVehicles.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                                                             .Where(agv => _IsTraving(agv.CurrentRunningTask()) || _IsInThisRegion(agv))
                                                             .Where(agv => agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetRegion().Name == regionQuery.Name))
                                                             .ToList();
                //如果該區域已經有車輛在該區域，且該區域已經達到最大容量
                if (goToRegionVehicles.Any() && goToRegionVehicles.Count() >= _Region.MaxVehicleCapacity)
                {
                    inRegionVehicles = goToRegionVehicles.Select(agv => agv.Name).ToList();
                    return false;
                }

            }
            catch (Exception ex)
            {
                LOG.ERROR(ex.Message, ex);
            }
            inRegionVehicles = otherVehicles.Where(agv => agv.currentMapPoint.GetRegion().Name == regionQuery.Name)
                                                                                 .Select(agv => agv.Name).ToList();

            var currentWillEntryRegionVehicleNames = _Region.ReserveRegionVehicles.Where(vehicleName => vehicleName != WannaEntryRegionVehicle.Name);
            return inRegionVehicles.Count() < _Region.MaxVehicleCapacity && currentWillEntryRegionVehicleNames.Count() < _Region.MaxVehicleCapacity;


            bool _IsTraving(TaskBase taskBase)
            {
                return taskBase.Stage == VehicleMovementStage.Traveling ||
                    taskBase.Stage == VehicleMovementStage.Traveling_To_Source ||
                    taskBase.Stage == VehicleMovementStage.Traveling_To_Destine ||
                    taskBase.Stage == VehicleMovementStage.Traveling_To_Region_Wait_Point;
            }

            bool _IsInThisRegion(IAGV agv)
            {
                return agv.currentMapPoint.GetRegion().Name == _Region.Name;
            }
        }

    }

    public static class Extensions
    {
        public static IEnumerable<MapRegion> GetRegions(this IEnumerable<MapPoint> path)
        {
            return path.Select(pt => pt.GetRegion())
                .DistinctBy(reg => reg.Name);
        }
    }
}

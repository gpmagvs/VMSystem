using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;

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
            var regionToGo = finalMapPoint.GetRegion(StaMap.Map);
            regionToGo.ReserveRegionVehicles.Add(vehicle.Name);
        }



        internal static void UpdateRegion(IAGV vehicle)
        {
            var agvPreviousRegions = GetRegions().Where(region => region.InRegionVehicles.Contains(vehicle.Name));
            foreach (var _region in agvPreviousRegions)
                _region.InRegionVehicles.Remove(vehicle.Name);
            vehicle.currentMapPoint.GetRegion(StaMap.Map).InRegionVehicles.Add(vehicle.Name);
        }

        internal static bool TryGetRegionMaxCapcity(MapPoint goalPoint, out int MaxVehicleCapacity)
        {
            MaxVehicleCapacity = goalPoint.GetRegion(StaMap.Map).MaxVehicleCapacity;
            return MaxVehicleCapacity != -1;
        }

        internal static bool TryGetRegionEntryPoints(MapPoint goalPoint, out IEnumerable<MapPoint> entryPoints)
        {
            entryPoints = goalPoint.GetRegion(StaMap.Map).EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag));
            return entryPoints.Any();
        }

        internal static bool TryGetRegionLeavePoints(MapPoint goalPoint, out IEnumerable<MapPoint> leavePoints)
        {
            leavePoints = goalPoint.GetRegion(StaMap.Map).LeavingTags.Select(tag => StaMap.GetPointByTagNumber(tag));
            return leavePoints.Any();
        }
        internal static IEnumerable<MapPoint> GetRegionEntryPoints(MapPoint goalPoint)
        {
            return GetRegionEntryPoints(goalPoint.GetRegion(StaMap.Map));
        }
        internal static IEnumerable<MapPoint> GetRegionEntryPoints(MapRegion nextRegion)
        {
            return nextRegion.EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag));

        }

        internal static bool IsRegionEnterable(IAGV WannaEntryRegionVehicle, MapRegion regionQuery, out List<string> inRegionVehicles)
        {
            inRegionVehicles = new List<string>();
            MapRegion _Region = StaMap.Map.Regions.FirstOrDefault(reg => reg.Name == regionQuery.Name);
            if (_Region == null)
                return true;
            inRegionVehicles = _Region.InRegionVehicles.Where(vehicleName => vehicleName != WannaEntryRegionVehicle.Name).ToList();
            var currentWillEntryRegionVehicleNames = _Region.ReserveRegionVehicles.Where(vehicleName => vehicleName != WannaEntryRegionVehicle.Name);
            return inRegionVehicles.Count() < _Region.MaxVehicleCapacity && currentWillEntryRegionVehicleNames.Count() < _Region.MaxVehicleCapacity;
        }
    }

    public static class Extensions
    {
        public static IEnumerable<MapRegion> GetRegions(this IEnumerable<MapPoint> path)
        {
            return path.Select(pt => pt.GetRegion(StaMap.Map))
                .DistinctBy(reg => reg.Name);
        }
    }
}

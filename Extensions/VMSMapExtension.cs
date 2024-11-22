using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;

namespace VMSystem.Extensions
{
    public static class VMSMapExtension
    {
        public static string GetDisplayAtCurrentMap(this int tagNumber)
        {
            return StaMap.GetPointByTagNumber(tagNumber)?.Graph.Display ?? string.Empty;
        }

        public static bool IsEntryPointOfWorkStation(this int tagNumber, out IEnumerable<MapPoint> workStations)
        {
            workStations = new List<MapPoint>();
            if (!StaMap.TryGetPointByTagNumber(tagNumber, out MapPoint pt))
                return false;
            workStations = pt.TargetWorkSTationsPoints();
            return workStations.Any();
        }
    }
}

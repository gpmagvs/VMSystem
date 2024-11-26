using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;

namespace VMSystem.Extensions
{
    public static class VMSMapExtension
    {
        public static string GetDisplayAtCurrentMap(this int tagNumber)
        {
            return tagNumber.GetMapPoint()?.Graph.Display ?? string.Empty;
        }
        public static MapPoint GetMapPoint(this int tagNumber)
        {
            return StaMap.GetPointByTagNumber(tagNumber);
        }

        public static bool IsRackPortStation(this int tagNumber)
        {
            var statationtype = tagNumber.GetMapPoint()?.StationType;
            return statationtype == MapPoint.STATION_TYPE.Buffer || statationtype == MapPoint.STATION_TYPE.Charge_Buffer;
        }
        public static bool IsEntryPointOfWorkStation(this int tagNumber, out IEnumerable<MapPoint> workStations)
        {
            workStations = new List<MapPoint>();
            if (!StaMap.TryGetPointByTagNumber(tagNumber, out MapPoint pt))
                return false;
            workStations = pt.TargetWorkSTationsPoints();
            return workStations.Any();
        }

        /// <summary>
        /// 取得該位置鄰近的工作站
        /// </summary>
        /// <param name="mapPt"></param>
        /// <returns></returns>
        public static List<MapPoint> GetNearByWorkStationAndEntryPoint(this MapPoint mapPt)
        {
            bool isNormalPt = mapPt.StationType == MapPoint.STATION_TYPE.Normal;
            if (isNormalPt)
                return mapPt.TargetNormalPoints().SelectMany(pt => pt.TargetWorkSTationsPoints()).ToList();
            else
                return mapPt.TargetNormalPoints().SelectMany(pt => pt.TargetNormalPoints().SelectMany(pt => pt.TargetWorkSTationsPoints())).ToList();
        }
    }
}

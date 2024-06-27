using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public partial class MoveTaskDynamicPathPlanV2
    {
        public static class LowLevelSearch
        {
            private static Map _Map => StaMap.Map;

            /// <summary>
            /// 最優路徑搜尋，不考慮任何constrain.
            /// </summary>
            /// <param name="StartPoint"></param>
            /// <param name="GoalPoint"></param>
            /// <returns></returns>
            /// <exception cref="Exceptions.NotFoundAGVException"></exception>
            public static IEnumerable<MapPoint> GetOptimizedMapPoints(MapPoint StartPoint, MapPoint GoalPoint, IEnumerable<MapPoint>? constrains, double VehicleCurrentAngle = double.MaxValue)
            {
                PathFinder _pathFinder = new PathFinder();
                clsPathInfo _pathInfo = _pathFinder.FindShortestPath(_Map, StartPoint, GoalPoint, new PathFinderOption
                {
                    OnlyNormalPoint = true,
                    ConstrainTags = constrains == null ? new List<int>() : constrains.GetTagCollection().ToList(),
                    Strategy = PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE,
                    VehicleCurrentAngle = VehicleCurrentAngle,
                    Algorithm = PathFinderOption.ALGORITHM.Dijsktra
                });

                if (_pathInfo == null || !_pathInfo.stations.Any())
                    throw new NoPathForNavigatorException($"Not any path found from {StartPoint.TagNumber} to {GoalPoint.TagNumber}");

                return _pathInfo?.stations;
            }

            public static bool TryGetOptimizedMapPointWithConstrains(ref IEnumerable<MapPoint> originalPath, IEnumerable<MapPoint> constrains, out IEnumerable<MapPoint> newPath)
            {
                newPath = new List<MapPoint>();
                var start = originalPath.First();
                var end = originalPath.Last();

                PathFinder _pathFinder = new PathFinder();
                clsPathInfo _pathInfo = _pathFinder.FindShortestPath(_Map, start, end, new PathFinderOption
                {
                    OnlyNormalPoint = true,
                    ConstrainTags = constrains.Select(pt => pt.TagNumber).ToList()
                });
                if (_pathInfo == null || !_pathInfo.stations.Any())
                {
                    return false;
                }
                newPath = _pathInfo.stations;
                return true;
            }
        }
    }

}

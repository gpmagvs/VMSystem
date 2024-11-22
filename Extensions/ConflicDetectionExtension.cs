using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;

namespace VMSystem.Extensions
{
    public static class ConflicDetectionExtension
    {
        /// <summary>
        /// 取出車輛名稱集合字串
        /// </summary>
        /// <param name="agvList"></param>
        /// <returns></returns>
        public static string GetNames(this IEnumerable<IAGV> agvList)
        {
            return string.Join(",", agvList.DistinctBy(agv => agv.Name).Where(agv => agv != null).Select(agv => agv.Name));
        }

        /// <summary>
        /// 取得站點名稱
        /// </summary>
        /// <param name="mapPoint"></param>
        /// <returns></returns>
        public static string? GetName(this MapPoint? mapPoint)
        {
            return mapPoint?.Graph.Display;
        }
    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.MAP;
using System.Drawing;
using VMSystem.AGV;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.Tools
{
    public class NavigationTools
    {

        /// <summary>
        /// 計算航向角度
        /// </summary>
        /// <param name="startPt"></param>
        /// <param name="endPt"></param>
        /// <returns></returns>
        public static double CalculationForwardAngle(PointF startPt, PointF endPt)
        {

            double deltaX = endPt.X - startPt.X;
            double deltaY = endPt.Y - startPt.Y;
            double angleInRadians = Math.Atan2(deltaY, deltaX);
            double angleInDegrees = angleInRadians * (180 / Math.PI);
            // 將角度調整到 -180 至 180 度的範圍
            if (angleInDegrees > 180)
            {
                angleInDegrees -= 360;
            }
            return angleInDegrees;

        }

        /// <summary>
        ///  計算航向角度
        /// </summary>
        /// <param name="startCoordination"></param>
        /// <param name="endCoordination"></param>
        /// <returns></returns>
        internal static double CalculationForwardAngle(clsCoordination startCoordination, clsCoordination endCoordination)
        {
            return CalculationForwardAngle(new PointF((float)startCoordination.X, (float)startCoordination.Y), new PointF((float)endCoordination.X, (float)endCoordination.Y));
        }


        /// <summary>
        /// 搜尋會與指定點位干涉的AGV
        /// </summary>
        /// <param name="naving_agv"></param>
        /// <param name="point"></param>
        /// <param name="interferenceAGVList"></param>
        /// <returns></returns>
        internal static bool TryFindInterferenceAGVOfPoint(IAGV naving_agv, MapPoint point, out List<IAGV> interferenceAGVList)
        {
            interferenceAGVList = new List<IAGV>();
            var agv_distance_from_secondaryPt = VMSManager.AllAGV.FilterOutAGVFromCollection(naving_agv.Name).Where(agv => agv.currentMapPoint.StationType == STATION_TYPE.Normal).ToDictionary(agv => agv, agv => agv.currentMapPoint.CalculateDistance(point));
            var tooNearAgvDistanc = agv_distance_from_secondaryPt.Where(kp => kp.Value <= naving_agv.options.VehicleLength / 100.0);
            interferenceAGVList = tooNearAgvDistanc.Select(kp => kp.Key).ToList();
            return interferenceAGVList.Count > 0;
        }

        internal static bool TryFindInterferenceAGVOfPoint(IAGV naving_agv, IEnumerable<MapPoint> points, out Dictionary<MapPoint, List<IAGV>> interferenceMapPoints)
        {
            interferenceMapPoints = new Dictionary<MapPoint, List<IAGV>>();

            foreach (var point in points)
            {
                if (TryFindInterferenceAGVOfPoint(naving_agv, point, out var agvList))
                {
                    interferenceMapPoints.Add(point,agvList);
                }
            }

            return interferenceMapPoints.Count > 0;
        }


    }
}

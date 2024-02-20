using System.Drawing;
using VMSystem.AGV;
using Newtonsoft.Json.Linq;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;

namespace VMSystem.TrafficControl
{
    public class Tools
    {

        // <summary>
        /// 計算干涉
        /// </summary>
        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, IEnumerable<IAGV> _OthersAGV, out IEnumerable<IAGV> ConflicAGVList)
        {
            ConflicAGVList = new List<IAGV>();
            bool is_SinglePoint = _Path.Count() == 1;
            List<MapPoint> pathPoints = _Path.ToList();
            if (is_SinglePoint)
            {
                return false;
            }
            else
            {
                //將每個路徑段用矩形表示
                double vehicleWidth = _UsePathAGV.options.VehicleWidth / 100.0;
                double vehicleLength = _UsePathAGV.options.VehicleLength / 100.0;
                List<MapRectangle> _PathRectangles = new List<MapRectangle>();
                for (int i = 0; i < pathPoints.Count() - 1; i++)
                {
                    var startPt = pathPoints[i];
                    var endPt = pathPoints[i + 1];
                    MapRectangle _rectangle = CreatePathRectangle(new PointF((float)startPt.X, (float)startPt.Y), new PointF((float)endPt.X, (float)endPt.Y), (float)vehicleWidth, (float)vehicleLength);
                    _rectangle.StartPointTag = startPt;
                    _rectangle.EndPointTag = endPt;
                    _PathRectangles.Add(_rectangle);
                }
                Dictionary<IAGV, MapRectangle> _OthersAGVRectangles = _OthersAGV.ToDictionary(agv => agv, agv => agv.AGVGeometery);
                var pathConflics = _PathRectangles.ToDictionary(reg => reg, reg => _OthersAGVRectangles.Where(agv => agv.Value.IsIntersectionTo(reg)));
                bool isInterfernce=_PathRectangles.Any(path => _OthersAGVRectangles.Any(agv => agv.Value.IsIntersectionTo(path)));
                pathConflics = pathConflics.Where(kp => kp.Value.Count() != 0).ToDictionary(k => k.Key, k => k.Value);
                ConflicAGVList = pathConflics.Values.SelectMany(v => v.Select(vv => vv.Key)).ToList().Distinct();
                return ConflicAGVList.Count() > 0;
            }
        }

        /// <summary>
        /// 計算干涉
        /// </summary>
        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, IEnumerable<IAGV> _OthersAGV)
        {
            return CalculatePathInterference(_Path, _UsePathAGV, _OthersAGV, out _);
        }
        public static MapRectangle CreateAGVRectangle(IAGV AGV)
        {
            var angleDegrees = (float)AGV.states.Coordination.Theta;
            var length = AGV.options.VehicleLength / 100.0;
            var width = AGV.options.VehicleWidth / 100.0;
            var center = new PointF((float)AGV.states.Coordination.X, (float)AGV.states.Coordination.Y);
            // 角度轉換為弧度
            float angleRadians = angleDegrees * (float)Math.PI / 180.0f;
            // 長度和寬度的一半
            float halfLength = (float)(length / 2);
            float halfWidth = (float)(width / 2);

            // 計算相對於中心的角點偏移
            PointF[] corners = new PointF[4];
            float cosTheta = (float)Math.Cos(angleRadians);
            float sinTheta = (float)Math.Sin(angleRadians);

            corners[0] = new PointF(center.X + cosTheta * halfLength - sinTheta * halfWidth,  // 左上
                                    center.Y + sinTheta * halfLength + cosTheta * halfWidth);
            corners[1] = new PointF(center.X - cosTheta * halfLength - sinTheta * halfWidth,  // 左下
                                    center.Y - sinTheta * halfLength + cosTheta * halfWidth);
            corners[2] = new PointF(center.X - cosTheta * halfLength + sinTheta * halfWidth,  // 右下
                                    center.Y - sinTheta * halfLength - cosTheta * halfWidth);
            corners[3] = new PointF(center.X + cosTheta * halfLength + sinTheta * halfWidth,  // 右上
                                    center.Y + sinTheta * halfLength - cosTheta * halfWidth);

            return new MapRectangle
            {
                Corner1 = corners[0],
                Corner2 = corners[1],
                Corner3 = corners[2],
                Corner4 = corners[3]
            };
        }

        public static PointF RotatePoint(PointF point, PointF center, double angleDegrees)
        {
            double angleRadians = angleDegrees * Math.PI / 180.0;
            double cosTheta = Math.Cos(angleRadians);
            double sinTheta = Math.Sin(angleRadians);

            return new PointF(
                center.X + (float)((point.X - center.X) * cosTheta - (point.Y - center.Y) * sinTheta),
                center.Y + (float)((point.X - center.X) * sinTheta + (point.Y - center.Y) * cosTheta)
            );
        }
        #region Private Methods
        public static MapRectangle CreatePathRectangle(PointF start, PointF end, float width, float length)
        {
            MapRectangle rectangle = new MapRectangle();
            // 計算方向向量
            PointF direction = new PointF(end.X - start.X, end.Y - start.Y);
            float magnitude = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            // 單位方向向量
            PointF unitDirection = new PointF(direction.X / magnitude, direction.Y / magnitude);
            // 單位法線向量
            PointF unitNormal = new PointF(-unitDirection.Y, unitDirection.X);
            // 計算四個角點
            rectangle.Corner1 = new PointF(start.X - unitDirection.X * length / 2 + unitNormal.X * width / 2,
                                         start.Y - unitDirection.Y * length / 2 + unitNormal.Y * width / 2);
            rectangle.Corner2 = new PointF(end.X + unitDirection.X * length / 2 + unitNormal.X * width / 2,
                                         end.Y + unitDirection.Y * length / 2 + unitNormal.Y * width / 2);
            rectangle.Corner3 = new PointF(end.X + unitDirection.X * length / 2 - unitNormal.X * width / 2,
                                            end.Y + unitDirection.Y * length / 2 - unitNormal.Y * width / 2);
            rectangle.Corner4 = new PointF(start.X - unitDirection.X * length / 2 - unitNormal.X * width / 2,
                                           start.Y - unitDirection.Y * length / 2 - unitNormal.Y * width / 2);
            return rectangle;
        }


        #endregion

        #region Classes
        #endregion
    }
}

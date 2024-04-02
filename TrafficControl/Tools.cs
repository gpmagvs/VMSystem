using System.Drawing;
using VMSystem.AGV;
using Newtonsoft.Json.Linq;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using System.Numerics;
using VMSystem.VMS;
using AGVSystemCommonNet6.Log;

namespace VMSystem.TrafficControl
{
    public class Tools
    {
        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, out IEnumerable<IAGV> ConflicAGVList)
        {
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(_UsePathAGV);
            return CalculatePathInterference(_Path, _UsePathAGV, otherAGV, out ConflicAGVList);
        }

        // <summary>
        /// 計算干涉
        /// </summary>
        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, IEnumerable<IAGV> _OthersAGV, out IEnumerable<IAGV> ConflicAGVList)
        {
            ConflicAGVList = new List<IAGV>();
            bool is_SinglePoint = _Path.Count() == 1;
            int indexOfAGVLoc = _Path.ToList().FindIndex(pt => pt.TagNumber == _UsePathAGV.states.Last_Visited_Node);
            List<MapPoint> pathPoints = _Path.Skip(indexOfAGVLoc).ToList();
            if (is_SinglePoint)
            {
                return false;
            }
            else
            {
                //將每個路徑段用矩形表示
                double vehicleWidth = _UsePathAGV.options.VehicleWidth / 100.0;
                double vehicleLength = _UsePathAGV.options.VehicleLength / 100.0;
                List<MapRectangle> _PathRectangles = GetPathRegionsWithRectangle(pathPoints, vehicleWidth, vehicleLength);
                Dictionary<IAGV, MapRectangle> _OthersAGVRectangles = _OthersAGV.ToDictionary(agv => agv, agv => agv.AGVGeometery);
                Dictionary<IAGV, List<MapRectangle>> _OthersAGVPathRectangles = _OthersAGV.ToDictionary(agv => agv, agv => GetPathRegionsWithRectangle(agv.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent.AGVRequestState.RemainTagList, agv.options.VehicleWidth / 100, agv.options.VehicleLength / 100));

                IEnumerable<MapRectangle> allOthersAGVPathRectangles = _OthersAGVPathRectangles.Values.SelectMany(re => re);

                var pathConflics = _PathRectangles.ToDictionary(reg => reg, reg => _OthersAGVRectangles.Where(agv => agv.Value.IsIntersectionTo(reg)));
                pathConflics = pathConflics.Where(kp => kp.Value.Count() != 0).ToDictionary(k => k.Key, k => k.Value);
                ConflicAGVList = pathConflics.Values.SelectMany(v => v.Select(vv => vv.Key)).ToList().Distinct();
                
                bool isAGVPathConflic = _PathRectangles.Any(rectangle => allOthersAGVPathRectangles.Any(_rectangle => rectangle.IsIntersectionTo(_rectangle)));
                
                bool isAGVGeometryConflic = ConflicAGVList.Count() > 0;


                return isAGVGeometryConflic || isAGVPathConflic;
            }
        }

        private static List<MapRectangle> GetPathRegionsWithRectangle(List<int> remainTagList, double vehicleWidth, double vehicleLength)
        {
            List<MapPoint> pathPoints = remainTagList.Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
            return GetPathRegionsWithRectangle(pathPoints, vehicleWidth, vehicleLength);
        }

        private static List<MapRectangle> GetPathRegionsWithRectangle(List<MapPoint> pathPoints, double vehicleWidth, double vehicleLength)
        {
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

            return _PathRectangles;
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

        public static Dictionary<DateTime, MapPoint> CalculateVehiclePathBaseTimeSeries(List<MapPoint> points, float V, float R)
        {
            Dictionary<DateTime, MapPoint> path = new Dictionary<DateTime, MapPoint>();
            Vector2 currentDirection = new Vector2(1, 0);  // 假設車輛初始方向朝東
            DateTime time = DateTime.Now;
            for (int i = 0; i < points.Count - 1; i++)
            {
                MapPoint start = points[i];
                MapPoint end = points[i + 1];

                // 計算旋轉所需時間
                float angle = CalculateAngle(start, end, currentDirection);
                int rotationTime = (int)MathF.Ceiling(MathF.Abs(angle) / R);

                // 在旋轉時間內座標不變
                for (int t = 0; t < rotationTime; t++)
                {
                    path.Add(time, new MapPoint { X = start.X, Y = start.Y });
                    time = time.AddSeconds(1);
                }

                // 更新方向
                currentDirection = Vector2.Normalize(new Vector2((float)(end.X - start.X), (float)(end.Y - start.Y)));

                // 直線移動至下一點
                Vector2 direction = new Vector2((float)(end.X - start.X), (float)(end.Y - start.Y));
                float distance = direction.Length();
                direction /= distance;
                int moveTime = (int)(distance / V);

                for (int t = 1; t <= moveTime; t++)
                {
                    MapPoint newPos = new MapPoint
                    {
                        X = start.X + direction.X * V * t,
                        Y = start.Y + direction.Y * V * t
                    };
                    path.Add(time, newPos);
                    time = time.AddSeconds(1);
                }
            }

            // 添加最後一點
            path.Add(time, points[points.Count - 1]);

            return path;
        }

        #region Private Methods

        // 計算兩點間的角度（以度為單位）
        private static float CalculateAngle(MapPoint from, MapPoint to, Vector2 currentDirection)
        {
            Vector2 toDirection = new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y));
            toDirection = Vector2.Normalize(toDirection);

            float dot = Vector2.Dot(currentDirection, toDirection);
            float det = currentDirection.X * toDirection.Y - currentDirection.Y * toDirection.X;
            float angle = MathF.Atan2(det, dot);

            return angle * (180 / MathF.PI);
        }

        #endregion



    }
}

using System.Drawing;
using VMSystem.AGV;
using Newtonsoft.Json.Linq;
using AGVSystemCommonNet6.MAP;

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
                List<MapRectTangle> _PathRectangles = new List<MapRectTangle>();
                for (int i = 0; i < pathPoints.Count() - 1; i++)
                {
                    var startPt = pathPoints[i];
                    var endPt = pathPoints[i + 1];
                    MapRectTangle _rectangle = CreatePathRectangle(new PointF((float)startPt.X, (float)startPt.Y), new PointF((float)endPt.X, (float)endPt.Y), (float)vehicleWidth, (float)vehicleLength);
                    _rectangle.StartPointTag = startPt;
                    _rectangle.EndPointTag = endPt;
                    _PathRectangles.Add(_rectangle);
                }
                Dictionary<IAGV, MapRectTangle> _OthersAGVRectangles = _OthersAGV.ToDictionary(agv => agv, agv => CreateAGVRectangle(agv));
                var pathConflics = _PathRectangles.ToDictionary(reg => reg, reg => _OthersAGVRectangles.Where(agv => agv.Value.IsIntersectionTo(reg)));

                pathConflics = pathConflics.Where(kp => kp.Value.Count() != 0).ToDictionary(k => k.Key, k => k.Value);
                ConflicAGVList=pathConflics.Values.SelectMany(v => v.Select(vv => vv.Key)).ToList().Distinct();
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
        public static MapRectTangle CreateAGVRectangle(IAGV AGV)
        {
            var agv_theta = AGV.states.Coordination.Theta;
            double agv_length = AGV.options.VehicleLength / 100.0;//m
            double agv_width = AGV.options.VehicleWidth / 100.0;//m

            PointF agv_coordination = new PointF((float)AGV.states.Coordination.X, (float)AGV.states.Coordination.Y);
            PointF[] rectangle = new PointF[4];
            // 计算未旋转的四个顶点
            rectangle[0] = new PointF((float)(agv_coordination.X - agv_length / 2), (float)(agv_coordination.Y + agv_width / 2));
            rectangle[1] = new PointF((float)(agv_coordination.X + agv_length / 2), (float)(agv_coordination.Y + agv_width / 2));
            rectangle[2] = new PointF((float)(agv_coordination.X + agv_length / 2), (float)(agv_coordination.Y - agv_width / 2));
            rectangle[3] = new PointF((float)(agv_coordination.X - agv_length / 2), (float)(agv_coordination.Y - agv_width / 2));

            // 将每个点绕中心点旋转
            for (int i = 0; i < rectangle.Length; i++)
            {
                rectangle[i] = RotatePoint(rectangle[i], agv_coordination, agv_theta);
            }

            return new MapRectTangle()
            {
                TopLeft = rectangle[0],
                TopRight = rectangle[1],
                BottomRight = rectangle[2],
                BottomLeft = rectangle[3]
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
        public static MapRectTangle CreatePathRectangle(PointF start, PointF end, float width, float length)
        {
            // 计算方向向量
            PointF direction = new PointF(end.X - start.X, end.Y - start.Y);
            // 计算单位方向向量
            float magnitude = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            PointF unitDirection = new PointF(direction.X / magnitude, direction.Y / magnitude);

            // 计算法线向量（单位法线）
            PointF normal = new PointF(-unitDirection.Y, unitDirection.X);

            // 根据车辆长度调整起点和终点
            PointF adjustedStart = new PointF(start.X - unitDirection.X * length / 2, start.Y - unitDirection.Y * length / 2);
            PointF adjustedEnd = new PointF(end.X + unitDirection.X * length / 2, end.Y + unitDirection.Y * length / 2);

            // 使用法线和宽度计算矩形的四个顶点，按照左上->右上->右下->左下的顺序
            MapRectTangle rectangle = new MapRectTangle();
            rectangle.TopLeft = new PointF(adjustedEnd.X - normal.X * width / 2, adjustedEnd.Y - normal.Y * width / 2); // Top-left
            rectangle.TopRight = new PointF(adjustedStart.X - normal.X * width / 2, adjustedStart.Y - normal.Y * width / 2);  // Top-right
            rectangle.BottomRight = new PointF(adjustedStart.X + normal.X * width / 2, adjustedStart.Y + normal.Y * width / 2); // Bottom-right
            rectangle.BottomLeft = new PointF(adjustedEnd.X + normal.X * width / 2, adjustedEnd.Y + normal.Y * width / 2); // Bottom-left

            return rectangle;
        }


        public static double CalculateTheta(PointF point1, PointF point2)
        {
            // 计算两点形成的向量的x和y分量
            float deltaX = point2.X - point1.X;
            float deltaY = point2.Y - point1.Y;

            // 使用Math.Atan2计算角度（结果为弧度）
            double thetaRadians = Math.Atan2(deltaY, deltaX);
            // 如果需要，可以将结果转换为度
            double thetaDegrees = thetaRadians * (180.0 / Math.PI);

            return thetaDegrees; // 返回的角度值
        }
        #endregion

        #region Classes
        public class MapRectTangle
        {
            public MapRectTangle() { }
            public PointF CenterPoint
            {
                get
                {
                    PointF[] rect = new PointF[4] { TopLeft, TopRight, BottomRight, BottomLeft };
                    float sumX = 0, sumY = 0;
                    foreach (var point in rect)
                    {
                        sumX += point.X;
                        sumY += point.Y;
                    }
                    return new PointF(sumX / rect.Length, sumY / rect.Length);
                }
            }
            public double Length
            {
                get
                {
                    PointF[] rect = new PointF[4] { TopLeft, TopRight, BottomRight, BottomLeft };
                    double diffX = TopRight.X - TopLeft.X;
                    double diffY = TopRight.Y - TopLeft.Y;
                    return Math.Sqrt(Math.Pow(diffX, 2) + Math.Pow(diffY, 2));
                }
            }


            public double Theta
            {
                get
                {
                    float deltaX = TopRight.X - TopLeft.X;
                    float deltaY = TopRight.Y - TopLeft.Y;
                    double thetaRadians = Math.Atan2(deltaY, deltaX);//弧度
                    double thetaDegrees = thetaRadians * (180.0 / Math.PI);//角度
                    return thetaDegrees;
                }
            }
            public PointF TopLeft { get; set; } = new PointF();
            public PointF TopRight { get; set; } = new PointF();
            public PointF BottomLeft { get; set; } = new PointF();
            public PointF BottomRight { get; set; } = new PointF();
            public MapPoint StartPointTag { get; set; } = new MapPoint();
            public MapPoint EndPointTag { get; set; } = new MapPoint();
            public bool IsIntersectionTo(MapRectTangle rectangle)
            {
                // 获取当前矩形和另一个矩形的边界
                float leftA = Math.Min(TopLeft.X, BottomLeft.X);
                float rightA = Math.Max(TopRight.X, BottomRight.X);
                float topA = Math.Max(TopLeft.Y, TopRight.Y);
                float bottomA = Math.Min(BottomLeft.Y, BottomRight.Y);

                float leftB = Math.Min(rectangle.TopLeft.X, rectangle.BottomLeft.X);
                float rightB = Math.Max(rectangle.TopRight.X, rectangle.BottomRight.X);
                float topB = Math.Max(rectangle.TopLeft.Y, rectangle.TopRight.Y);
                float bottomB = Math.Min(rectangle.BottomLeft.Y, rectangle.BottomRight.Y);

                bool isVerticalOverlap = !(topA > topB && bottomA > topB || topA < bottomB && bottomA < bottomB);
                bool isHorizontalOverlap = !(rightA > rightB && leftA > rightB || leftA < leftB && rightA < leftB);


                return isHorizontalOverlap && isVerticalOverlap;
            }
        }
        #endregion
    }
}

using System.Drawing;
using VMSystem.AGV;
using Newtonsoft.Json.Linq;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using System.Numerics;
using VMSystem.VMS;
using AGVSystemCommonNet6.Log;

using AGVSystemCommonNet6.Configuration;
using System.Linq;
using AGVSystemCommonNet6;
using Newtonsoft.Json;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using static AGVSystemCommonNet6.MAP.MapPoint;
using VMSystem.AGV.TaskDispatch.Tasks;
using AGVSystemCommonNet6.Microservices.AGVS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.TrafficControl
{
    public class clsInterferenceCalculateParameters
    {
        /// <summary>
        /// unit:m
        /// </summary>
        public double VehicleWidthExpandSizeOfNormalMove { get; set; } = 0.5;
        public double VehicleLengthExpandSizeOfNormalMove { get; set; } = 0.5;


        public double VehicleWidthExpandSizeOfLeaveWorkStation { get; set; } = 0.5;
        public double VehicleLengthExpandSizeOfLeaveWorkStation { get; set; } = 0.5;




    }

    public class Tools
    {
        public static clsInterferenceCalculateParameters Parameters { get; set; } = new clsInterferenceCalculateParameters();

        public static void LoadParametersFromJsonFile(string jsonFilePath)
        {
            if (File.Exists(jsonFilePath))
            {
                try
                {
                    Parameters = JsonConvert.DeserializeObject<clsInterferenceCalculateParameters>(File.ReadAllText(jsonFilePath));
                }
                catch (Exception ex)
                {
                    LOG.Critical($"Read Navi Tools Parameters fail. {ex.Message}:{ex.StackTrace}");
                }
            }

            File.WriteAllText(jsonFilePath, Parameters.ToJson());
        }

        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, out IEnumerable<IAGV> ConflicAGVList, bool IsAGVBackward, double expandRatio = 1)
        {
            if (_Path.Count() == 0)
            {
                ConflicAGVList = new List<IAGV>();
                return false;
            }
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(_UsePathAGV);
            return CalculatePathInterference(_Path, _UsePathAGV, otherAGV, out ConflicAGVList, IsAGVBackward, expandRatio: expandRatio);
        }
        public static bool CalculatePathInterferenceByAGVGeometry(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, out IEnumerable<IAGV> ConflicAGVList, double expandRatio = 1)
        {
            if (_Path.Count() == 0)
            {
                ConflicAGVList = new List<IAGV>();
                return false;
            }
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(_UsePathAGV.Name).ToList();
            _UsePathAGV.IsDirectionHorizontalTo(otherAGV.First());
            var cannotPassToAgvCollection = otherAGV.Where(agv => !agv.IsDirectionHorizontalTo(_UsePathAGV));
            return CalculatePathInterference(_Path, _UsePathAGV, otherAGV, out ConflicAGVList, false, NoConsiderOtherAGVRemainPath: true);
        }
        // <summary>
        /// 計算干涉
        /// </summary>
        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, IEnumerable<IAGV> _OthersAGV, out IEnumerable<IAGV> ConflicAGVList, bool IsAGVBackward,
            bool NoConsiderOtherAGVRemainPath = false, double expandRatio = 1)
        {
            //if (AGVSConfigulator.SysConfigs.TaskControlConfigs.UnLockEntryPointWhenParkAtEquipment)
            //{
            //    _OthersAGV = _OthersAGV.Where(agv => !agv.currentMapPoint.IsEquipment);
            //}
            //_OthersAGV = _OthersAGV.Where(agv=>agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
            var thetaOfUsePathAGV = _UsePathAGV.states.Coordination.Theta;
            var _RotaionRegion = _UsePathAGV.AGVRotaionGeometry;

            ConflicAGVList = new List<IAGV>();
            bool is_SinglePoint = _Path.Count() == 1;
            int indexOfAGVLoc = _Path.ToList().FindIndex(pt => pt.TagNumber == _UsePathAGV.states.Last_Visited_Node);
            List<MapPoint> pathPoints = _Path.Skip(indexOfAGVLoc).ToList();

            bool TryGetConflicAGVByRotation(MapCircleArea agvRotationRegion, out IEnumerable<IAGV> _ConflicAGVList)
            {
                _ConflicAGVList = _OthersAGV.Where(agv => agvRotationRegion.IsIntersectionTo(agv.AGVRotaionGeometry) || agvRotationRegion.IsIntersectionTo(agv.AGVRealTimeGeometery));
                return _ConflicAGVList.Count() != 0;
            }

            if (is_SinglePoint)
            {
                bool isAgvWillRotation = Math.Abs(thetaOfUsePathAGV - _Path.Last().Direction) > 5;
                if (isAgvWillRotation && !IsAGVBackward)
                {
                    return TryGetConflicAGVByRotation(_RotaionRegion, out ConflicAGVList);

                }
                return false;
            }
            else
            {
                var thetaOfNextPath = Tools.CalculationForwardAngle(_Path.First().ToCoordination(), _Path.Skip(1).Take(1).First().ToCoordination());
                bool isAgvWillRotation = Math.Abs(thetaOfUsePathAGV - thetaOfNextPath) > 5;

                //將每個路徑段用矩形表示
                double vehicleWidth = _UsePathAGV.options.VehicleWidth * expandRatio / 100.0;
                double vehicleLength = _UsePathAGV.options.VehicleLength * expandRatio / 100.0;
                List<MapRectangle> _PathRectangles = GetPathRegionsWithRectangle(pathPoints, vehicleWidth, vehicleLength);

                List<int> _GetRemainTagsOfAGV(IAGV agv)
                {
                    var outputs = new List<int>();
                    var trafficState = agv.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent.AGVRequestState;
                    var _remainTags = trafficState.NextSequenceTaskRemainTagList;
                    if (!_remainTags.Any())
                        return new List<int>();
                    //trafficState.SequenceTaskTrajectoryList
                    var firstTagOfReminTags = _remainTags.First();

                    outputs.Add(agv.states.Last_Visited_Node);
                    outputs.AddRange(_remainTags);
                    return outputs;
                }

                Dictionary<IAGV, List<MapRectangle>> _OthersAGVPathRectangles = NoConsiderOtherAGVRemainPath ?
                    _OthersAGV.ToDictionary(agv => agv, agv => new List<MapRectangle> { agv.AGVRealTimeGeometery }) :
                    _OthersAGV.ToDictionary(agv => agv, agv => GetPathRegionsWithRectangle(_GetRemainTagsOfAGV(agv), agv.options.VehicleWidth / 100, agv.options.VehicleLength / 100));

                if (isAgvWillRotation && !IsAGVBackward)
                {
                    ConflicAGVList = _OthersAGV.Where(agv => _RotaionRegion.IsIntersectionTo(agv.AGVRotaionGeometry) || _RotaionRegion.IsIntersectionTo(agv.AGVRealTimeGeometery));
                    var _pathConflicOtherAGVsRemainPath = _OthersAGVPathRectangles.Values.Select(rectangles => rectangles.Any(rectangle => _RotaionRegion.IsIntersectionTo(rectangle)));
                    int indexOfLastConflicRegion = _pathConflicOtherAGVsRemainPath.ToList().FindLastIndex(r => r == true);
                    bool conflicSoFar = false;
                    if (indexOfLastConflicRegion >= 0)
                    {
                        var lastPathRegion = _OthersAGVPathRectangles.Values.ToList()[indexOfLastConflicRegion];
                        var lastPathRegionOwner = _OthersAGVPathRectangles.FirstOrDefault(kp => kp.Value == lastPathRegion).Key;
                        conflicSoFar = lastPathRegion.Last().StartPointTag.CalculateDistance(lastPathRegionOwner.states.Coordination.X, lastPathRegionOwner.states.Coordination.Y) > 5;
                    }
                    //if (conflicSoFar)
                    //    return false;
                    if ((TryGetConflicAGVByRotation(_RotaionRegion, out ConflicAGVList) || _pathConflicOtherAGVsRemainPath.Any(ret => ret)))
                        return true;
                }

                Dictionary<IAGV, MapRectangle> _OthersAGVRectangles = _OthersAGV.ToDictionary(agv => agv, agv => agv.AGVRealTimeGeometery);

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

        public static List<MapRectangle> GetPathRegionsWithRectangle(List<MapPoint> pathPoints, double vehicleWidth, double vehicleLength)
        {
            List<MapRectangle> _PathRectangles = new List<MapRectangle>();
            for (int i = 0; i < pathPoints.Count() - 1; i++)
            {
                var startPt = pathPoints[i];
                var endPt = pathPoints[i + 1];

                var startPtRegion = startPt.GetRegion(StaMap.Map);
                var endPtRegion = startPt.GetRegion(StaMap.Map);

                bool isNarrow = startPtRegion.IsNarrowPath || endPtRegion.IsNarrowPath;

                MapRectangle _rectangle = CreatePathRectangle(new PointF((float)startPt.X, (float)startPt.Y), new PointF((float)endPt.X, (float)endPt.Y), (float)vehicleWidth + (isNarrow ? 0.1f : 0f), (float)vehicleLength);
                _rectangle.StartPointTag = startPt;
                _rectangle.EndMapPoint = endPt;
                _PathRectangles.Add(_rectangle);
            }

            return _PathRectangles;
        }

        /// <summary>
        /// 計算干涉
        /// </summary>
        public static bool CalculatePathInterference(IEnumerable<MapPoint> _Path, IAGV _UsePathAGV, IEnumerable<IAGV> _OthersAGV, bool IsAGVBackward)
        {
            return CalculatePathInterference(_Path, _UsePathAGV, _OthersAGV, out _, IsAGVBackward);
        }
        internal static MapRectangle CreateSquare(MapPoint mapPoint, double sideLength)
        {
            return CreateSquare(new PointF((float)mapPoint.X, (float)mapPoint.Y), sideLength);
        }
        public static MapRectangle CreateSquare(PointF center, double sideLength)
        {
            // 創建正方形的四個角點
            PointF corner1 = new PointF((float)(center.X - sideLength / 2), (float)(center.Y - sideLength / 2));
            PointF corner2 = new PointF((float)(center.X + sideLength / 2), (float)(center.Y - sideLength / 2));
            PointF corner3 = new PointF((float)(center.X + sideLength / 2), (float)(center.Y + sideLength / 2));
            PointF corner4 = new PointF((float)(center.X - sideLength / 2), (float)(center.Y + sideLength / 2));

            // 創建新的 MapRectangle 對象
            MapRectangle square = new MapRectangle
            {
                Corner1 = corner1,
                Corner2 = corner2,
                Corner3 = corner3,
                Corner4 = corner4,
                // 可以根據需要設置其他屬性
            };

            return square;
        }

        public static MapRectangle CreateRectangle(double x, double y, double theta, double width, double length)
        {

            var center = new PointF((float)x, (float)y);
            // 角度轉換為弧度
            float angleRadians = (float)theta * (float)Math.PI / 180.0f;
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

        public static MapRectangle CreateAGVRectangle(IAGV AGV)
        {
            bool isInNarrowRegion = AGV.currentMapPoint.GetRegion(StaMap.Map).IsNarrowPath;
            var angleDegrees = (float)AGV.states.Coordination.Theta;

            bool isInWorkStation = AGV.currentMapPoint.StationType != STATION_TYPE.Normal;
            bool isInChargeStation = AGV.currentMapPoint.IsCharge;
            var length = AGV.options.VehicleLength / 100.0;
            var width = AGV.options.VehicleWidth / 100.0 * (isInChargeStation ? 0.5 : 1);

            MapRectangle _rectangle = CreateRectangle(AGV.states.Coordination.X, AGV.states.Coordination.Y, AGV.states.Coordination.Theta, width, length);

            _rectangle.StartPointTag = AGV.currentMapPoint;
            _rectangle.EndMapPoint = AGV.currentMapPoint;
            return _rectangle;
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
        public static double CalculationForwardAngle(MapPoint startPoint, MapPoint endPoint)
        {
            PointF startPF = new((float)startPoint.X, (float)startPoint.Y);
            PointF endPF = new((float)endPoint.X, (float)endPoint.Y);
            return CalculationForwardAngle(startPF, endPF);
        }

        internal static double CalculateWorkStationStopAngle(int workstationTag, int speficEntryTag = -1)
        {
            var workStation = StaMap.GetPointByTagNumber(workstationTag);
            var indexsOfTarget = workStation.Target.Keys;
            IEnumerable<MapPoint> secondaryPoints = indexsOfTarget.Select(_index => StaMap.GetPointByIndex(_index));
            if (secondaryPoints.Count() == 0)
                return 0;
            MapPoint _fromPoint = null;
            if (speficEntryTag != -1)
            {
                _fromPoint = secondaryPoints.FirstOrDefault(pt => pt.TagNumber == speficEntryTag);
            }
            else
            {
                _fromPoint = secondaryPoints.FirstOrDefault();
            }
            PointF _fromP = new PointF((float)_fromPoint.X, (float)_fromPoint.Y);
            PointF _toP = new PointF((float)workStation.X, (float)workStation.Y);
            return CalculationForwardAngle(_fromP, _toP);
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
                    interferenceMapPoints.Add(point, agvList);
                }
            }

            return interferenceMapPoints.Count > 0;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="goal"></param>
        /// <param name="agv"></param>
        /// <returns>回傳直如果為double.MaxValue視為找不到路徑或是目標站點不予許車種</returns>
        public static double ElevateDistanceToGoalStation(MapPoint _workStationPoint, IAGV agv)
        {
            var entryPoints = _workStationPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            var validStations = entryPoints.SelectMany(pt => pt.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
            Task<Dictionary<int, int>> AcceptAGVInfoOfEQTags = AGVSSerivces.TRANSFER_TASK.GetEQAcceptAGVTypeInfo(validStations.Select(pt => pt.TagNumber));//key:tag , value :車款
            AcceptAGVInfoOfEQTags.Wait();
            var v = AcceptAGVInfoOfEQTags.Result.Where(x => x.Key == _workStationPoint.TagNumber).Select(x => x.Value).FirstOrDefault();
            var acceptTypeOfEq = (AGV_TYPE)v;
            if (acceptTypeOfEq != AGV_TYPE.Any && acceptTypeOfEq != agv.model)
                return double.MaxValue;

            PathFinder pathFinder = new PathFinder();
            var result = pathFinder.FindShortestPath(StaMap.Map, agv.currentMapPoint, _workStationPoint, new PathFinder.PathFinderOption { OnlyNormalPoint = false });
            if (result == null)
                return double.MaxValue;
            else
                return result.total_travel_distance;
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

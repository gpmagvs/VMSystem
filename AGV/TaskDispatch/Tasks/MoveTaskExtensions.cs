using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using System.Drawing;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public static class MoveTaskExtensions
    {
        public enum GOAL_ARRIVALE_CHECK_STATE
        {
            OK,
            REGISTED,
            WILL_COLLIOUS_WHEN_ARRIVE
        }
        /// <summary>
        /// 取得最終要抵達的點
        /// </summary>
        /// <param name="orderInfo"></param>
        /// <returns></returns>
        public static MapPoint GetFinalMapPoint(this clsTaskDto orderInfo, IAGV executeAGV, VehicleMovementStage stage)
        {

            int tagOfFinalGoal = 0;
            ACTION_TYPE _OrderAction = orderInfo.Action;

            if (_OrderAction == ACTION_TYPE.None) //移動訂單
                tagOfFinalGoal = orderInfo.To_Station_Tag;
            else //工作站訂單
            {
                int _workStationTag = 0;
                if (_OrderAction == ACTION_TYPE.Load || _OrderAction == ACTION_TYPE.Carry) //搬運訂單，要考慮當前是要作取或或是放貨
                {
                    if (stage == VehicleMovementStage.Traveling_To_Destine)
                    {
                        if (orderInfo.need_change_agv)
                            _workStationTag = orderInfo.TransferToTag;
                        else
                            _workStationTag = orderInfo.To_Station_Tag;
                    }
                    else
                        _workStationTag = orderInfo.From_Station_Tag;
                }
                else //僅取貨或是放貨
                {
                    _workStationTag = orderInfo.To_Station_Tag;
                }

                MapPoint _workStationPoint = StaMap.GetPointByTagNumber(_workStationTag);

                List<MapPoint> entryPoints = _workStationPoint.Target.Keys
                                                                     .Select(index => StaMap.GetPointByIndex(index))
                                                                     .ToList();

                List<int> forbidTags = executeAGV.GetForbidPassTagByAGVModel();
                List<MapPoint> validPoints = entryPoints.Where(points => !forbidTags.Contains(points.TagNumber)).ToList();
                MapPoint pt = validPoints.FirstOrDefault();
                if (pt == null)
                {

                }
                return pt;

            }
            return StaMap.GetPointByTagNumber(tagOfFinalGoal);
        }


        public static double FinalForwardAngle(this IEnumerable<MapPoint> path)
        {
            var _path = path.Where(pt => pt != null).ToList();
            if (!_path.Any() || _path.Count() < 2)
            {
                return !_path.Any() ? 0 : _path.Last().Direction;
            }
            var lastPt = _path.Last();
            var lastSecondPt = _path.First();
            clsCoordination lastCoord = new clsCoordination(lastPt.X, lastPt.Y, 0);
            clsCoordination lastSecondCoord = new clsCoordination(lastSecondPt.X, lastSecondPt.Y, 0);
            return Tools.CalculationForwardAngle(lastSecondCoord, lastCoord);
        }


        public static IEnumerable<MapPoint> TargetNormalPoints(this MapPoint mapPoint)
        {
            return mapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index))
                .Where(pt => StaMap.Map.Points.Values.Any(_pt => _pt.TagNumber == pt.TagNumber))
                .Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal);
        }
        public static IEnumerable<MapPoint> TargetWorkSTationsPoints(this MapPoint mapPoint)
        {
            return mapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index))
                .Where(pt => StaMap.Map.Points.Values.Any(_p => _p.TagNumber == pt.TagNumber))
                .Where(pt => pt.StationType != MapPoint.STATION_TYPE.Normal);
        }
        public static IEnumerable<MapPoint> TargetParkableStationPoints(this MapPoint mapPoint)
        {
            IEnumerable<MapPoint> stations = mapPoint.TargetWorkSTationsPoints();
            return stations.Where(pt => pt.IsParking);
        }
        public static IEnumerable<MapPoint> TargetParkableStationPoints(this MapPoint mapPoint, ref IAGV AgvToPark)
        {
            IEnumerable<MapPoint> stations = mapPoint.TargetParkableStationPoints();
            //所有被註冊的Tag
            var registedTags = StaMap.RegistDictionary.Keys.ToList();
            List<int> _forbiddenTags = AgvToPark.GetCanNotReachTags();
            _forbiddenTags.AddRange(registedTags);
            return stations.Where(pt => pt.IsParking && !_forbiddenTags.Contains(pt.TagNumber));
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="path"></param>
        /// <param name="refOrderInfo"></param>
        /// <param name="stage"></param>
        /// <returns></returns>
        public static double GetStopDirectionAngle(this IEnumerable<MapPoint> path, clsTaskDto refOrderInfo, IAGV executeAGV, VehicleMovementStage stage, MapPoint nextStopPoint)
        {
            var finalStopPoint = refOrderInfo.GetFinalMapPoint(executeAGV, stage);
            var _path = path.Where(pt => pt != null).ToList();
            //先將各情境角度算好來
            //1. 朝向最後行駛方向
            double _finalForwardAngle = _path.FinalForwardAngle();

            double _narrowPathDirection(MapPoint stopPoint)
            {
                var settingIdleAngle = stopPoint.GetRegion().ThetaLimitWhenAGVIdling;
                double stopAngle = settingIdleAngle;
                if (settingIdleAngle == 90)
                {
                    if (executeAGV.states.Coordination.Theta >= 0 && executeAGV.states.Coordination.Theta <= 180)
                    {
                        stopAngle = settingIdleAngle;
                    }
                    else
                    {

                        stopAngle = settingIdleAngle - 180;
                    }

                }
                else if (settingIdleAngle == 0)
                {
                    if (executeAGV.states.Coordination.Theta >= -90 && executeAGV.states.Coordination.Theta <= 90)
                    {
                        stopAngle = settingIdleAngle;
                    }
                    else
                    {

                        stopAngle = settingIdleAngle - 180;
                    }
                }
                return stopAngle;

            }


            bool isPathEndPtIsDestine = _path.Last().TagNumber == finalStopPoint?.TagNumber;

            if (isPathEndPtIsDestine)
            {

                if (stage == VehicleMovementStage.AvoidPath)
                {
                    return path.Last().Direction_Avoid;
                }

                if (refOrderInfo.Action == ACTION_TYPE.None && stage != VehicleMovementStage.AvoidPath_Park)
                {
                    var fintailStopPt = StaMap.GetPointByTagNumber(finalStopPoint.TagNumber).Clone();
                    if (!nextStopPoint.IsNarrowPath || stage == VehicleMovementStage.AvoidPath || stage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
                    {
                        if (stage == VehicleMovementStage.AvoidPath || stage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
                        {
                            return fintailStopPt.Direction_Avoid;
                        }
                        else
                            return fintailStopPt.Direction;
                    }
                    return _narrowPathDirection(nextStopPoint);
                }
                else
                {
                    MapPoint WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.From_Station_Tag);
                    if (stage == VehicleMovementStage.Traveling_To_Destine)
                    {
                        if (refOrderInfo.need_change_agv)
                            WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.TransferToTag);
                        else
                            WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.To_Station_Tag);
                    }
                    if (stage == VehicleMovementStage.AvoidPath_Park)
                    {
                        WorkStation = executeAGV.NavigationState.AvoidActionState.AvoidPt;
                    }
                    return (new MapPoint[2] { finalStopPoint, WorkStation }).FinalForwardAngle();
                }
            }
            else
            {
                if (nextStopPoint.IsNarrowPath)
                    return _narrowPathDirection(nextStopPoint);
                else
                    return _finalForwardAngle;
            }
        }

        public static double DirectionToPoint(this IAGV agv, MapPoint point)
        {
            var endPt = new PointF((float)point.X, (float)point.Y);
            var startPt = new PointF((float)agv.states.Coordination.X, (float)agv.states.Coordination.Y);
            return Tools.CalculationForwardAngle(startPt, endPt);
        }
        public static List<int> GetForbidPassTagByAGVModel(this IAGV agv)
        {
            return StaMap.GetNoStopTagsByAGVModel(agv.model);
        }

        public static bool IsPathHasAnyYieldingPoints(this IEnumerable<MapPoint> points, out IEnumerable<MapPoint> yieldedPoints)
        {
            yieldedPoints = new List<MapPoint>();
            if (points != null && points.Any())
            {
                yieldedPoints = points.Where(pt => pt.IsTrafficCheckPoint);
                return yieldedPoints.Any();
            }
            else
                return false;
        }

        public static bool IsPathHasPointsBeRegisted(this IEnumerable<MapPoint> points, IAGV pathOwner, out IEnumerable<MapPoint> registedPoints)
        {
            registedPoints = new List<MapPoint>();
            if (points != null && points.Any())
            {
                var registedTags = StaMap.RegistDictionary.Where(pair => points.Select(p => p.TagNumber).Contains(pair.Key))
                                                            .Where(pair => pair.Value.RegisterAGVName != pathOwner.Name)
                                                            .Select(pair => pair.Key);
                registedPoints = points.Where(point => registedTags.Contains(point.TagNumber));
                return registedPoints.Any();
            }
            else
                return false;
        }


        public static bool IsPathConflicWithOtherAGVBody(this IEnumerable<MapPoint> path, IAGV pathOwner, out IEnumerable<IAGV> conflicAGVList)
        {
            conflicAGVList = new List<IAGV>();
            var othersAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(pathOwner);
            if (path == null || !path.Any())
            {
                conflicAGVList = othersAGV.Where(_agv => _agv.AGVRotaionGeometry.IsIntersectionTo(pathOwner.AGVRotaionGeometry));
                return conflicAGVList.Any();
            }

            var finalCircleRegion = path.Last().GetCircleArea(ref pathOwner);

            conflicAGVList = othersAGV.Where(agv => agv.AGVRotaionGeometry.IsIntersectionTo(finalCircleRegion));

            if (conflicAGVList.Any())
                return true;
            return Tools.CalculatePathInterferenceByAGVGeometry(path, pathOwner, out conflicAGVList);
        }

        public static bool IsRemainPathConflicWithOtherAGVBody(this IEnumerable<MapPoint> path, IAGV pathOwner, out IEnumerable<IAGV> conflicAGVList)
        {

            conflicAGVList = new List<IAGV>();
            var agvIndex = path.ToList().FindIndex(pt => pt.TagNumber == pathOwner.currentMapPoint.TagNumber);
            var width = pathOwner.options.VehicleWidth / 100.0;
            var length = pathOwner.options.VehicleLength / 100.0;
            var pathRegion = Tools.GetPathRegionsWithRectangle(path.Skip(agvIndex).ToList(), width, length);

            var otherAGVs = VMSManager.AllAGV.FilterOutAGVFromCollection(pathOwner);
            var conflicAgvs = otherAGVs.Where(agv => pathRegion.Any(segment => segment.IsIntersectionTo(agv.AGVRealTimeGeometery)));

            //get conflic segments 
            var conflicPaths = pathRegion.Where(segment => conflicAgvs.Any(agv => segment.IsIntersectionTo(agv.AGVRealTimeGeometery)));
            return conflicPaths.Any();

        }

        public static bool IsDirectionIsMatchToRegionSetting(this IAGV Agv, out double regionSetting, out double diff)
        {
            regionSetting = 0;
            diff = 0;
            var currentMapRegion = Agv.currentMapPoint.GetRegion();
            if (currentMapRegion == null) return true;

            var agvTheta = Agv.states.Coordination.Theta;
            regionSetting = currentMapRegion.ThetaLimitWhenAGVIdling;
            // 確定角度差異，調整為介於0和180度之間
            double angleDifference = Math.Abs(agvTheta - regionSetting);
            angleDifference = angleDifference > 180 ? 360 - angleDifference : angleDifference;
            diff = Math.Abs(angleDifference);
            return diff >= -5 && diff <= 5 || diff >= 175 && diff <= 180;
        }

        public static bool CanVehiclePassTo(this IAGV Agv, IAGV otherAGV)
        {
            double Agv1X = Agv.states.Coordination.X;
            double Agv1Y = Agv.states.Coordination.Y;
            double Agv1Theta = Agv.states.Coordination.Theta;
            double Agv1Width = Agv.options.VehicleWidth;
            double Agv1Length = Agv.options.VehicleLength;

            double Agv2X = otherAGV.states.Coordination.X;
            double Agv2Y = otherAGV.states.Coordination.Y;
            double Agv2Theta = otherAGV.states.Coordination.Theta;
            double Agv2Width = otherAGV.options.VehicleWidth;
            double Agv2Length = otherAGV.options.VehicleLength;


            // 計算兩車的中心點距離
            double distance = Math.Sqrt(Math.Pow(Agv1X - Agv2X, 2) + Math.Pow(Agv1Y - Agv2Y, 2));

            // 確定角度差異，調整為介於0和180度之間
            double angleDifference = Math.Abs(Agv1Theta - Agv2Theta);
            angleDifference = angleDifference > 180 ? 360 - angleDifference : angleDifference;

            // 考慮角度差異進行碰撞檢測，這裡僅為示例，實際應用需更複雜的幾何計算
            if (angleDifference == 0 || angleDifference == 180)
            {
                // 兩車平行行駛
                return distance >= (Agv1Width + Agv2Width);
            }
            else if (angleDifference == 90 || angleDifference == 270)
            {
                // 兩車垂直行駛
                return distance >= (Agv1Length + Agv2Length);
            }
            else
            {
                // 其他角度，進行簡化的交點計算
                return distance >= (Agv1Width + Agv2Width) * Math.Sin(angleDifference * Math.PI / 180);
            }
        }


        public static bool IsArrivable(this MapPoint destine, IAGV wannaGoVehicle, out GOAL_ARRIVALE_CHECK_STATE checkState)
        {
            checkState = GOAL_ARRIVALE_CHECK_STATE.OK;

            bool _IsRegisted()
            {
                if (!StaMap.RegistDictionary.TryGetValue(destine.TagNumber, out var registInfo))
                    return false;
                return registInfo.RegisterAGVName != wannaGoVehicle.Name;
            }

            if (_IsRegisted())
            {
                checkState = GOAL_ARRIVALE_CHECK_STATE.REGISTED;
                return false;
            }




            return true;
        }
        public static IEnumerable<MapRectangle> GetPathRegion(this IEnumerable<MapPoint> path, IAGV pathOwner, double widthExpand = 0, double lengthExpand = 0)
        {
            var v_width = (pathOwner.options.VehicleWidth / 100.0) + widthExpand;
            var v_length = (pathOwner.options.VehicleLength / 100.0) + lengthExpand;
            if (path.Count() <= 1)
            {
                return new List<MapRectangle>() {
                    Tools.CreateAGVRectangle(pathOwner)
                };
            }
            return Tools.GetPathRegionsWithRectangle(path.ToList(), v_width, v_length);
        }
        public static double[] GetCornerThetas(this IEnumerable<MapPoint> path)
        {

            if (path.Count() < 3)
                return new double[0];

            int numderOfCorner = path.Count() - 2;
            var _points = path.ToList();
            List<double> results = new List<double>();
            for (int i = 0; i < numderOfCorner; i++)
            {
                double[] pStart = new double[2] { _points[i].X, _points[i].Y };
                double[] pMid = new double[2] { _points[i + 1].X, _points[i + 1].Y };
                double[] pEnd = new double[2] { _points[i + 2].X, _points[i + 2].Y };
                double theta = CalculateAngle(pStart[0], pStart[1], pMid[0], pMid[1], pEnd[0], pEnd[1]);
                results.Add(180 - theta);
            }

            return results.ToArray();
            //3 1 4 2

            double CalculateAngle(double xA, double yA, double xB, double yB, double xC, double yC)
            {
                // 計算向量AB和向量BC
                double ABx = xB - xA;
                double ABy = yB - yA;
                double BCx = xC - xB;
                double BCy = yC - yB;

                // 計算點積和向量的模
                double dotProduct = ABx * BCx + ABy * BCy;
                double magAB = Math.Sqrt(ABx * ABx + ABy * ABy);
                double magBC = Math.Sqrt(BCx * BCx + BCy * BCy);

                // 計算角度
                double angle = Math.Acos(dotProduct / (magAB * magBC)) * (180.0 / Math.PI);  // 轉換為度
                return angle;
            }
        }

        /// <summary>
        /// 取得區域內的所有點位
        /// </summary>
        /// <param name="region"></param>
        /// <returns></returns>
        public static List<MapPoint> GetPointsInRegion(this MapRegion region)
        {
            return StaMap.Map.Points.Values.Where(pt => pt.GetRegion().Name == region.Name)
                                            .ToList();
        }

        public static MapPoint GetNearestPointOfRegion(this MapRegion region, IAGV agvToGo)
        {
            var pointsOfRegion = region.GetPointsInRegion();
            return pointsOfRegion.Where(pt => !pt.IsVirtualPoint && pt.StationType == MapPoint.STATION_TYPE.Normal)
                                 .OrderBy(pt => pt.CalculateDistance(agvToGo.states.Coordination)).First();
        }

        public static IEnumerable<MapPoint> GetParkablePointOfRegion(this MapRegion region, IAGV agvToGo)
        {
            var pointsOfRegion = region.GetPointsInRegion();
            return region.GetPointsInRegion().SelectMany(pt => pt.TargetParkableStationPoints(ref agvToGo));
        }

    }

}

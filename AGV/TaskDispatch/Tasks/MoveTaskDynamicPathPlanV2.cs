using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.MAP;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.TrafficControl.TrafficControlCenter;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 0428 動態路徑生程規劃開發
    /// </summary>
    public class MoveTaskDynamicPathPlanV2 : MoveTaskDynamicPathPlan
    {
        public MoveTaskDynamicPathPlanV2() : base()
        {
        }
        public MoveTaskDynamicPathPlanV2(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }
        public override void CreateTaskToAGV()
        {
            //base.CreateTaskToAGV();
        }
        public override bool IsAGVReachDestine => Agv.states.Last_Visited_Node == DestineTag;

        public class clsPathSearchResult
        {
            public bool IsConflicByNarrowPathDirection { get; set; }
            public bool isPathConflicByAGVGeometry { get; set; }

            public IEnumerable<IAGV> ConflicAGVCollection { get; set; }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public override async Task SendTaskToAGV()
        {
            IsTaskCanceled = false;
            PassedTags.Clear();

            MapPoint finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
            LowLevelSearch.GetOptimizedMapPoints(this.Agv.currentMapPoint, finalMapPoint);
            MapPoint _tempGoal = finalMapPoint;
            DestineTag = finalMapPoint.TagNumber;
            bool _isAagvAlreadyThereBegin = Agv.states.Last_Visited_Node == DestineTag;
            int _sequence = 0;
            bool _isTurningAngleDoneInNarrow = false;
            Stopwatch _searPathTimer = Stopwatch.StartNew();
            while (_isAagvAlreadyThereBegin || Agv.states.Last_Visited_Node != DestineTag && !IsTaskCanceled) //需考慮AGV已經在目的地
            {

                if (IsTaskCanceled)
                    return;
                (bool success, IEnumerable<MapPoint> optimizePath, clsPathSearchResult results) result = new(false, null, new clsPathSearchResult());



                IEnumerable<MapPoint> nextOptimizePath = new List<MapPoint>();

                if (_isAagvAlreadyThereBegin)
                {
                    nextOptimizePath = new List<MapPoint> { Agv.currentMapPoint };

                }
                else
                {
                    _searPathTimer.Restart();
                    while (!(result = await _SearchPassablePath(_tempGoal)).success)
                    {
                        RealTimeOptimizePathSearchReuslt = result.optimizePath;


                        //if (_searPathTimer.Elapsed.Seconds > 5)
                        //{
                        //    PathConflicRequest.CONFLIC_STATE conflicReason = result.results.IsConflicByNarrowPathDirection ?
                        //        PathConflicRequest.CONFLIC_STATE.NARROW_PATH_CONFLIC : PathConflicRequest.CONFLIC_STATE.REMAIN_PATH_COLLUSION_CONFLIC;
                        //    PathConflicSolveRequestInvoke(new TrafficControlCenter.PathConflicRequest(Agv,
                        //        result.results.ConflicAGVCollection.Distinct(),
                        //        result.optimizePath,
                        //        conflicReason));
                        //    return;
                        //}

                        if (IsTaskCanceled)
                            return;
                        if (result.results.IsConflicByNarrowPathDirection && !_isTurningAngleDoneInNarrow)
                        {
                            _isTurningAngleDoneInNarrow = await HandleAGVAtNarrowPath(_sequence, _isTurningAngleDoneInNarrow, result);
                            await Task.Delay(1000);
                            continue;

                        }

                        TrafficWaitingState.SetDisplayMessage($"(Search Path to Tag-{_tempGoal.TagNumber}...)");

                        await Task.Delay(100);
                        try
                        {
                            //取出下一個停止點
                            var otherAGVPoints = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv).Select(agv => agv.currentMapPoint);
                            var filterVirtualPoints = result.optimizePath.Where(pt => pt.TagNumber != Agv.currentMapPoint.TagNumber)
                                                                         .Where(pt => !pt.IsVirtualPoint) //濾除虛擬點
                                                                         .Where(pt => otherAGVPoints.All(agv_pt => agv_pt.CalculateDistance(pt) >= 2));//距離其他車輛2公尺以上
                            _tempGoal = filterVirtualPoints.Reverse().Skip(2).First();
                        }
                        catch (Exception ex)
                        {
                            _tempGoal = finalMapPoint;
                            continue;
                        }

                    }
                    RealTimeOptimizePathSearchReuslt = nextOptimizePath = result.optimizePath;

                    
                    async Task<(bool success, IEnumerable<MapPoint> optimizePath, clsPathSearchResult results)> _SearchPassablePath(MapPoint goal)
                    {
                        clsPathSearchResult _searchResult = new clsPathSearchResult();
                        bool PassableInNarrowPath(out IEnumerable<IAGV> conflicAGVCollection)
                        {
                            conflicAGVCollection = new List<IAGV>();

                            if (!Agv.currentMapPoint.IsNarrowPath)
                                return true;
                            var nonHorizontalAGVs = VMSManager.AllAGV.FilterOutAGVFromCollection(Agv)
                                                    .Where(_agv => _agv.currentMapPoint.IsNarrowPath)
                                                    .Where(_agv => !_agv.IsDirectionHorizontalTo(Agv));
                            conflicAGVCollection = nonHorizontalAGVs;
                            return !nonHorizontalAGVs.Any();
                        }

                        var optimizePath = LowLevelSearch.GetOptimizedMapPoints(this.Agv.currentMapPoint, goal);
                        bool isPathHasPointsBeRegisted = optimizePath.IsPathHasPointsBeRegisted(this.Agv, out var registed);
                        bool isHasAnyYieldPoints = optimizePath.IsPathHasAnyYieldingPoints(out var yieldPoints);
                        _searchResult.isPathConflicByAGVGeometry = optimizePath.IsPathConflicWithOtherAGVBody(this.Agv, out var conflicAGVListOfPathCollsion);
                        _searchResult.IsConflicByNarrowPathDirection = !PassableInNarrowPath(out IEnumerable<IAGV> conflicNarrowPathAGVCollection) && _searchResult.isPathConflicByAGVGeometry;

                        List<IAGV> conflicAGVs = new List<IAGV>();
                        conflicAGVs.AddRange(conflicAGVListOfPathCollsion);
                        conflicAGVs.AddRange(conflicNarrowPathAGVCollection);
                        _searchResult.ConflicAGVCollection = conflicAGVs;

                        bool HasOtherNewPath = false;
                        bool IsPathPassable = !isPathHasPointsBeRegisted && !_searchResult.isPathConflicByAGVGeometry && !_searchResult.IsConflicByNarrowPathDirection;

                        IEnumerable<MapPoint> secondaryPath = new List<MapPoint>();
                        if (!IsPathPassable)
                        {
                            List<MapPoint> constrains = new List<MapPoint>();
                            var othersAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(Agv);
                            var otherAgvPoints = othersAGV.Select(agv => agv.currentMapPoint);
                            constrains.AddRange(otherAgvPoints);

                            IEnumerable<MapPoint> AgvConflicAround(IAGV _agv)
                            {
                                return _agv.currentMapPoint.TargetNormalPoints()
                                                    .Where(pt => pt.GetCircleArea(ref _agv).IsIntersectionTo(_agv.AGVRotaionGeometry));
                            }


                            constrains.AddRange(othersAGV.SelectMany(agv => AgvConflicAround(agv)));
                            constrains = constrains.Where(pt => pt.TagNumber != Agv.currentMapPoint.TagNumber)
                                                   .Where(pt => pt.TagNumber != optimizePath.Last().TagNumber)
                                                   .ToList();

                            var OtherNewPathFound = LowLevelSearch.TryGetOptimizedMapPointWithConstrains(ref optimizePath, constrains, out secondaryPath);
                            bool isPathconflicOfSecondaryPath = secondaryPath.IsPathConflicWithOtherAGVBody(this.Agv, out conflicAGVListOfPathCollsion);
                            _searchResult.isPathConflicByAGVGeometry = isPathconflicOfSecondaryPath;
                            _searchResult.IsConflicByNarrowPathDirection = !PassableInNarrowPath(out conflicNarrowPathAGVCollection) && _searchResult.isPathConflicByAGVGeometry;


                            conflicAGVs.Clear();
                            conflicAGVs.AddRange(conflicAGVListOfPathCollsion);
                            conflicAGVs.AddRange(conflicNarrowPathAGVCollection);
                            _searchResult.ConflicAGVCollection = conflicAGVs;


                            if (OtherNewPathFound && !isPathconflicOfSecondaryPath && !_searchResult.IsConflicByNarrowPathDirection)
                            {

                                return (true, secondaryPath, _searchResult);
                            }

                        }
                        return (IsPathPassable && optimizePath.Last() != Agv.currentMapPoint, optimizePath, _searchResult);
                    }
                }

                _isTurningAngleDoneInNarrow = false;
                var nextPath = GetNextPath(new clsPathInfo
                {
                    stations = nextOptimizePath.ToList(),
                }, Agv.states.Last_Visited_Node, out bool IsNexPathHasEqReplcingParts, out int blockByEqPartsReplace, 4);

                var isNextPathGoalIsFinal = nextPath.Last() == finalMapPoint;


                double stopAngle = nextPath.GetStopDirectionAngle(OrderData, Agv, Stage, nextPath.Last());
                var pathToAGVSegment = PathFinder.GetTrajectory(StaMap.Map.Name, nextPath.ToList()).ToArray();
                pathToAGVSegment = pathToAGVSegment.SkipWhile(pt => _previsousTrajectorySendToAGV.Any(_pt => _pt.Point_ID == pt.Point_ID)).ToArray();

                _previsousTrajectorySendToAGV.AddRange(pathToAGVSegment);

                //產生丟給車載的數據模型
                clsTaskDownloadData _taskDownloadData = new clsTaskDownloadData
                {
                    Task_Name = OrderData.TaskName,
                    Task_Sequence = _sequence,
                    Action_Type = ACTION_TYPE.None,
                    Destination = finalMapPoint.TagNumber,
                };
                _taskDownloadData.Trajectory = _previsousTrajectorySendToAGV.ToArray();
                _taskDownloadData.Trajectory.Last().Theta = stopAngle;

                MoveTaskEvent = new clsMoveTaskEvent(Agv, nextOptimizePath.GetTagCollection(), nextPath.ToList(), false);
                StaMap.RegistPoint(Agv.Name, MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList, out string ErrorMessage);

                await base._DispatchTaskToAGV(_taskDownloadData);
                _sequence += 1;


                string GetDestineDisplay()
                {
                    int _destineTag = 0;
                    bool isCarryOrderAndGoToSource = OrderData.Action == ACTION_TYPE.Carry && Stage == VehicleMovementStage.Traveling_To_Source;
                    _destineTag = isCarryOrderAndGoToSource ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;
                    return StaMap.GetStationNameByTag(_destineTag);
                }

                int nearGoalTag = nextPath.Reverse()
                                         .Skip(isNextPathGoalIsFinal || nextPath.Count() <= 2 ? 0 : 1)
                                         .FirstOrDefault().TagNumber;


                UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(前往 Tag-{nearGoalTag}->{nextPath.Last().TagNumber})");
                while (!PassedTags.Contains(nearGoalTag))
                {

                    if (IsTaskCanceled)
                        return;
                    if (_isAagvAlreadyThereBegin)
                        break;
                    if (nearGoalTag == DestineTag)
                    {
                        while (Agv.states.Last_Visited_Node != DestineTag)
                        {
                            UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(即將抵達終點-{DestineTag})");
                            await Task.Delay(1);
                            if (IsTaskCanceled)
                                return;
                        }
                        return;
                    }
                    else
                    {
                        if (Agv.states.Last_Visited_Node == nearGoalTag || nextPath.Last().TagNumber == Agv.states.Last_Visited_Node)
                            break;
                    }
                    await Task.Delay(100);
                }
                _isAagvAlreadyThereBegin = false;
                _tempGoal = finalMapPoint;

                await Task.Delay(1);



            }

            //return base.SendTaskToAGV();
        }

        private async Task<bool> HandleAGVAtNarrowPath(int _sequence, bool _isTurningAngleDoneInNarrow, (bool success, IEnumerable<MapPoint> optimizePath, clsPathSearchResult results) result)
        {
            await SendCancelRequestToAGV();
            var newPath = new MapPoint[1] { Agv.currentMapPoint };
            var agvIndex = result.optimizePath.ToList().FindIndex(pt => pt.TagNumber == Agv.states.Last_Visited_Node);
            var pathForCaluStopAngle = result.optimizePath.Skip(agvIndex).Take(2);
            double _stopAngle = pathForCaluStopAngle.GetStopDirectionAngle(OrderData, Agv, Stage, pathForCaluStopAngle.Last());
            clsTaskDownloadData turnTask = new clsTaskDownloadData
            {
                Task_Name = OrderData.TaskName,
                Task_Sequence = _sequence,
                Action_Type = ACTION_TYPE.None,
                Destination = Agv.currentMapPoint.TagNumber,
            };
            turnTask.Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, newPath.ToList()).ToArray();
            turnTask.Trajectory.Last().Theta = _stopAngle;
            await base._DispatchTaskToAGV(turnTask);
            _previsousTrajectorySendToAGV.Clear();
            _isTurningAngleDoneInNarrow = true;
            return _isTurningAngleDoneInNarrow;
        }

        private void UpdateMoveStateMessage(string msg)
        {
            TrafficWaitingState.SetDisplayMessage(msg);
        }


        public struct LowLevelSearch
        {
            private static Map _Map => StaMap.Map;

            /// <summary>
            /// 最優路徑搜尋，不考慮任何constrain.
            /// </summary>
            /// <param name="StartPoint"></param>
            /// <param name="GoalPoint"></param>
            /// <returns></returns>
            /// <exception cref="Exceptions.NotFoundAGVException"></exception>
            public static IEnumerable<MapPoint> GetOptimizedMapPoints(MapPoint StartPoint, MapPoint GoalPoint)
            {
                PathFinder _pathFinder = new PathFinder();
                clsPathInfo _pathInfo = _pathFinder.FindShortestPath(_Map, StartPoint, GoalPoint, new PathFinderOption
                {
                    OnlyNormalPoint = true
                });

                if (_pathInfo == null || !_pathInfo.stations.Any())
                    throw new Exceptions.NotFoundAGVException($"Not any path found from {StartPoint.TagNumber} to {GoalPoint.TagNumber}");

                return _pathInfo.stations;
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

    public static class MoveTaskExtensions
    {
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
                if (_OrderAction == ACTION_TYPE.Carry) //搬運訂單，要考慮當前是要作取或或是放貨
                {
                    if (stage == VehicleMovementStage.Traveling_To_Destine)
                        _workStationTag = orderInfo.To_Station_Tag;
                    else
                        _workStationTag = orderInfo.From_Station_Tag;
                }
                else //僅取貨或是放貨
                {
                    _workStationTag = orderInfo.To_Station_Tag;
                }

                MapPoint _workStationPoint = StaMap.GetPointByTagNumber(_workStationTag);

                var entryPoints = _workStationPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
                var forbidTags = executeAGV.GetForbidPassTagByAGVModel();
                var validPoints = entryPoints.Where(points => !forbidTags.Contains(points.TagNumber));
                return validPoints.FirstOrDefault();

            }
            return StaMap.GetPointByTagNumber(tagOfFinalGoal);
        }


        public static double FinalForwardAngle(this IEnumerable<MapPoint> path)
        {

            if (!path.Any() || path.Count() < 2)
            {
                return !path.Any() ? 0 : path.Last().Direction;
            }
            var lastPt = path.Last();
            var lastSecondPt = path.First();
            clsCoordination lastCoord = new clsCoordination(lastPt.X, lastPt.Y, 0);
            clsCoordination lastSecondCoord = new clsCoordination(lastSecondPt.X, lastSecondPt.Y, 0);
            return Tools.CalculationForwardAngle(lastSecondCoord, lastCoord);
        }


        public static IEnumerable<MapPoint> TargetNormalPoints(this MapPoint mapPoint)
        {
            return mapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index))
                .Where(pt => StaMap.Map.Points.Values.Contains(pt))
                .Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal);
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

            //先將各情境角度算好來
            //1. 朝向最後行駛方向
            double _finalForwardAngle = path.FinalForwardAngle();

            double _narrowPathDirection(MapPoint stopPoint)
            {
                if (stopPoint == null)
                    throw new ArgumentNullException(nameof(stopPoint));
                if (!stopPoint.IsNarrowPath)
                    throw new Exception("非窄道點位");
                var nearNarrowPoints = stopPoint.TargetNormalPoints().Where(pt => pt.IsNarrowPath);
                if (!nearNarrowPoints.Any())
                    throw new Exception("鄰近位置沒有窄道點位");
                // 0 1 2 3 4 5
                int indexOfBeforeStopPoint = path.ToList().FindIndex(pt => pt.TagNumber == nextStopPoint.TagNumber) - 1;
                if (indexOfBeforeStopPoint < 0)
                {
                    //由圖資計算

                    return new MapPoint[2] { stopPoint, nearNarrowPoints.First() }.FinalForwardAngle();
                }
                return new MapPoint[2] { path.ToList()[indexOfBeforeStopPoint], stopPoint }.FinalForwardAngle();
            }


            bool isPathEndPtIsDestine = path.Last().TagNumber == finalStopPoint.TagNumber;


            if (isPathEndPtIsDestine)
            {
                if (refOrderInfo.Action == ACTION_TYPE.None)
                {
                    if (nextStopPoint.IsNarrowPath)
                        return _narrowPathDirection(nextStopPoint);
                    return finalStopPoint.Direction;
                }
                else
                {
                    MapPoint WorkStation = StaMap.GetPointByTagNumber(stage == VehicleMovementStage.Traveling_To_Destine ? refOrderInfo.To_Station_Tag : refOrderInfo.From_Station_Tag);
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


        public static IEnumerable<int> GetForbidPassTagByAGVModel(this IAGV agv)
        {
            List<int> tags = new List<int>();
            switch (agv.model)
            {
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.SUBMERGED_SHIELD:
                    tags = StaMap.Map.TagNoStopOfForkAGV;
                    break;
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.FORK:
                    tags = StaMap.Map.TagNoStopOfForkAGV;
                    break;
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.YUNTECH_FORK_AGV:
                    break;
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.INSPECTION_AGV:
                    break;
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.SUBMERGED_SHIELD_Parts:
                    break;
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.Any:
                    break;
                default:
                    break;
            }
            return tags;
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
            //var pathCircleAreas = path.Select(pt => pt.GetCircleArea(ref pathOwner));
            //var conclifsAGV = otherAGVCollection.Where(agv => pathCircleAreas.Any(circle => circle.IsIntersectionTo(agv.AGVRotaionGeometry)));
            //if(conclifsAGV.Any())
            //{
            //    conflicAGVList = conclifsAGV.ToList();
            //    return true;
            //}
            conflicAGVList = new List<IAGV>();

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
            var conflicAgvs = otherAGVs.Where(agv => pathRegion.Any(segment => segment.IsIntersectionTo(agv.AGVGeometery)));

            //get conflic segments 
            var conflicPaths = pathRegion.Where(segment => conflicAgvs.Any(agv => segment.IsIntersectionTo(agv.AGVGeometery)));
            return conflicPaths.Any();

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
    }

}

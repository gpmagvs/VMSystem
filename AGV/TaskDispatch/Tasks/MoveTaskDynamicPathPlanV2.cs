using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using RosSharp.RosBridgeClient.MessageTypes.Geometry;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using VMSystem.Dispatch;
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
        public int SeqIndex = 0;

        List<MapPoint> dynamicConstrains = new List<MapPoint>();

        public override async Task SendTaskToAGV()
        {
            Agv.OnMapPointChanged += Agv_OnMapPointChanged;
            try
            {

                MapPoint finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
                DestineTag = finalMapPoint.TagNumber;
                _previsousTrajectorySendToAGV = new List<clsMapPoint>();
                int _seq = 0;
                MapPoint searchStartPt = Agv.currentMapPoint;
                Agv.NavigationState.StateReset();

                while (_seq == 0 || DestineTag != Agv.currentMapPoint.TagNumber)
                {
                    await Task.Delay(500);
                    if (IsTaskCanceled || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                        throw new TaskCanceledException();
                    try
                    {
                        var dispatchCenterReturnPath = (await DispatchCenter.MoveToDestineDispatchRequest(Agv, searchStartPt, OrderData, Stage));
                        //var dispatchCenterReturnPath = (await DispatchCenter.MoveToGoalGetPath(Agv, searchStartPt, OrderData, Stage));
                        if (dispatchCenterReturnPath == null || !dispatchCenterReturnPath.Any())
                        {
                            searchStartPt = Agv.currentMapPoint;

                            //UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(Search Path...)");
                            await Task.Delay(500);
                            //if (_previsousTrajectorySendToAGV.Count > 0 && Agv.currentMapPoint.TagNumber != DestineTag)
                            //{
                            //    await SendCancelRequestToAGV();
                            //    while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                            //    {
                            //        UpdateMoveStateMessage($"Wait Cycle Stop Done..");
                            //        if (IsTaskCanceled)
                            //            throw new TaskCanceledException();
                            //        await Task.Delay(1000);
                            //    }
                            //    _previsousTrajectorySendToAGV.Clear();
                            //}
                            Agv.NavigationState.ResetNavigationPoints();
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            continue;
                        }
                        var nextPath = dispatchCenterReturnPath.ToList();
                        TrafficWaitingState.SetStatusNoWaiting();
                        var nextGoal = nextPath.Last();

                        var remainPath = nextPath.Where(pt => nextPath.IndexOf(nextGoal) >= nextPath.IndexOf(nextGoal));
                        Agv.NavigationState.UpdateNavigationPoints(nextPath);

                        nextPath.First().Direction = int.Parse(Math.Round(Agv.states.Coordination.Theta) + "");
                        var trajectory = PathFinder.GetTrajectory(CurrentMap.Name, nextPath.ToList());
                        trajectory = trajectory.Where(pt => !_previsousTrajectorySendToAGV.GetTagList().Contains(pt.Point_ID)).ToArray();
                        if (trajectory.Length == 0)
                            continue;

                        trajectory.Last().Theta = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, nextGoal);
                        _previsousTrajectorySendToAGV.AddRange(trajectory);
                        _previsousTrajectorySendToAGV = _previsousTrajectorySendToAGV.Distinct().ToList();


                        if (!StaMap.RegistPoint(Agv.Name, nextPath, out var msg))
                        {
                            await SendCancelRequestToAGV();
                            while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                            {
                                if (IsTaskCanceled)
                                    throw new TaskCanceledException();
                                await Task.Delay(500);
                            }
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            Agv.NavigationState.ResetNavigationPoints();
                            _previsousTrajectorySendToAGV.Clear();
                            searchStartPt = Agv.currentMapPoint;
                            continue;
                        }

                        //await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                        //while (!StaMap.RegistPoint(Agv.Name, nextPath, out var msg))
                        //{
                        //    await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                        //    Agv.NavigationState.ResetNavigationPoints();
                        //    UpdateMoveStateMessage($"Wait Regist Points Done...");
                        //    if (IsTaskCanceled)
                        //        throw new TaskCanceledException();
                        //    await Task.Delay(1000);
                        //}

                        await _DispatchTaskToAGV(new clsTaskDownloadData
                        {
                            Action_Type = ACTION_TYPE.None,
                            Task_Name = OrderData.TaskName,
                            Destination = Agv.NavigationState.RegionControlState == VehicleNavigationState.REGION_CONTROL_STATE.WAIT_AGV_REACH_ENTRY_POINT ? nextGoal.TagNumber : DestineTag,
                            Trajectory = _previsousTrajectorySendToAGV.ToArray(),
                            Task_Sequence = _seq
                        });
                        _seq += 1;
                        MoveTaskEvent = new clsMoveTaskEvent(Agv, nextPath.GetTagCollection(), nextPath.ToList(), false);
                        //UpdateMoveStateMessage($"Go to {nextGoal.TagNumber}");
                        int nextGoalTag = nextGoal.TagNumber;
                        MapPoint lastGoal = nextGoal;
                        int lastGoalTag = nextGoalTag;
                        try
                        {
                            lastGoal = nextPath[nextPath.Count - 2];
                            lastGoalTag = lastGoal.TagNumber;
                        }
                        catch (Exception)
                        {
                        }
                        searchStartPt = nextGoal;
                        UpdateMoveStateMessage($"前往-{nextGoal.Graph.Display}");
                        while (nextGoalTag != Agv.currentMapPoint.TagNumber)
                        {
                            if (IsTaskCanceled)
                            {
                                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                                {
                                    await Task.Delay(100);
                                    UpdateMoveStateMessage($"任務取消中...");
                                }
                                throw new TaskCanceledException();
                            }

                            if (lastGoalTag == Agv.currentMapPoint.TagNumber)
                            {
                                break;
                            }


                            if (Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                                throw new TaskCanceledException();

                            if (cycleStopRequesting)
                            {
                                cycleStopRequesting = false;
                                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                                {
                                    UpdateMoveStateMessage("Cycle Stoping");
                                    await Task.Delay(1000);
                                }
                                _previsousTrajectorySendToAGV.Clear();
                                break;
                            }

                            await Task.Delay(10);
                        }

                        _ = Task.Run(async () =>
                        {
                            UpdateMoveStateMessage($"抵達-{nextGoal.Graph.Display}");
                            await Task.Delay(1000);
                            TrafficWaitingState.SetStatusNoWaiting();
                            DispatchCenter.CancelDispatchRequest(Agv);
                        });
                    }
                    catch (TaskCanceledException ex)
                    {
                        throw ex;
                    }
                    catch (Exception ex)
                    {
                        LOG.ERROR(ex.Message, ex);
                        continue;
                    }

                }

            }
            catch (TaskCanceledException ex)
            {
                throw ex;
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
                Agv.NavigationState.ResetNavigationPoints();
            }
        }

        private void Agv_OnMapPointChanged(object? sender, int e)
        {
            List<int> _NavigationTags = Agv.NavigationState.NextNavigtionPoints.GetTagCollection().ToList();
            UpdateMoveStateMessage($"當前路徑:{string.Join("->", _NavigationTags)}");
        }

        internal override void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            base.HandleAGVNavigatingFeedback(feedbackData);
        }

        public IEnumerable<MapPoint> GetGoalsOfOptimizePath(MapPoint FinalDestine, List<MapPoint> dynamicConstrains)
        {
            List<MapPoint> goals = new List<MapPoint>();
            IEnumerable<MapPoint> optimizePathPlan = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, FinalDestine, dynamicConstrains);
            //var registedPoints = StaMap.GetRegistedPointsOfPath(optimizePathPlan.ToList(), Agv.Name);
            IEnumerable<MapPoint> checkPoints = optimizePathPlan.Where(pt => !dynamicConstrains.Contains(pt) && pt.IsTrafficCheckPoint);
            goals.AddRange(checkPoints);
            goals.Add(FinalDestine);
            if (goals.First() == Agv.currentMapPoint)
                goals.RemoveAt(0);
            return goals.Distinct();
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

        public void UpdateMoveStateMessage(string msg)
        {
            if (OrderData == null)
                return;
            string GetDestineDisplay()
            {
                int _destineTag = 0;
                bool isCarryOrderAndGoToSource = OrderData.Action == ACTION_TYPE.Carry && Stage == VehicleMovementStage.Traveling_To_Source;
                _destineTag = isCarryOrderAndGoToSource ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;
                return StaMap.GetStationNameByTag(_destineTag);
            }

            TrafficWaitingState.SetDisplayMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n({msg})");
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
            public static IEnumerable<MapPoint> GetOptimizedMapPoints(MapPoint StartPoint, MapPoint GoalPoint, IEnumerable<MapPoint> constrains)
            {
                PathFinder _pathFinder = new PathFinder();
                clsPathInfo _pathInfo = _pathFinder.FindShortestPath(_Map, StartPoint, GoalPoint, new PathFinderOption
                {
                    OnlyNormalPoint = true,
                    ConstrainTags = constrains.GetTagCollection().ToList(),
                    Strategy = PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE
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
                if (_OrderAction == ACTION_TYPE.Carry) //搬運訂單，要考慮當前是要作取或或是放貨
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
        public static IEnumerable<MapPoint> TargetWorkSTationsPoints(this MapPoint mapPoint)
        {
            return mapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index))
                .Where(pt => StaMap.Map.Points.Values.Contains(pt))
                .Where(pt => pt.StationType != MapPoint.STATION_TYPE.Normal);
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
                //if (stopPoint == null)
                //    throw new ArgumentNullException(nameof(stopPoint));
                //if (!stopPoint.IsNarrowPath)
                //    throw new Exception("非窄道點位");
                //var nearNarrowPoints = stopPoint.TargetNormalPoints().Where(pt => pt.IsNarrowPath);
                //if (!nearNarrowPoints.Any())
                //    throw new Exception("鄰近位置沒有窄道點位");
                //// 0 1 2 3 4 5
                //int indexOfBeforeStopPoint = path.ToList().FindIndex(pt => pt.TagNumber == nextStopPoint.TagNumber) - 1;
                //if (indexOfBeforeStopPoint < 0)
                //{
                //    //由圖資計算

                //    return new MapPoint[2] { stopPoint, nearNarrowPoints.First() }.FinalForwardAngle();
                //}
                //return new MapPoint[2] { path.ToList()[indexOfBeforeStopPoint], stopPoint }.FinalForwardAngle();
                var settingIdleAngle = stopPoint.GetRegion(StaMap.Map).ThetaLimitWhenAGVIdling;
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
                    MapPoint WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.From_Station_Tag);
                    if (stage == VehicleMovementStage.Traveling_To_Destine)
                    {
                        if (refOrderInfo.need_change_agv)
                            WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.TransferToTag);
                        else
                            WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.To_Station_Tag);
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
        public static IEnumerable<int> GetForbidPassTagByAGVModel(this IAGV agv)
        {
            List<int> tags = new List<int>();
            switch (agv.model)
            {
                case AGVSystemCommonNet6.clsEnums.AGV_TYPE.SUBMERGED_SHIELD:
                    tags = StaMap.Map.TagNoStopOfSubmarineAGV;
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
            var conflicAgvs = otherAGVs.Where(agv => pathRegion.Any(segment => segment.IsIntersectionTo(agv.AGVGeometery)));

            //get conflic segments 
            var conflicPaths = pathRegion.Where(segment => conflicAgvs.Any(agv => segment.IsIntersectionTo(agv.AGVGeometery)));
            return conflicPaths.Any();

        }

        public static bool IsDirectionIsMatchToRegionSetting(this IAGV Agv, out double regionSetting, out double diff)
        {
            regionSetting = 0;
            diff = 0;
            var currentMapRegion = Agv.currentMapPoint.GetRegion(StaMap.Map);
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

    }

}

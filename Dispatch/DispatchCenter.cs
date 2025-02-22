﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Notify;
using NLog;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;
using static AGVSystemCommonNet6.DATABASE.DatabaseCaches;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.TrafficControl.VehicleNavigationState;

namespace VMSystem.Dispatch
{
    public static class DispatchCenter
    {
        public enum GOAL_SELECT_METHOD
        {
            TO_GOAL_DIRECTLY,
            TO_POINT_INFRONT_OF_GOAL
        }
        internal static List<int> TagListOfWorkstationInPartsReplacing { get; private set; } = new List<int>();
        private static SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        internal static List<int> TagListOfInFrontOfPartsReplacingWorkstation = new List<int>();
        public static DeadLockMonitor TrafficDeadLockMonitor = new DeadLockMonitor();
        /// <summary>
        /// Key:設備TAG, Value: IAGV集合
        /// </summary>
        public static ConcurrentDictionary<int, List<IAGV>> AGVNavigationPauseStore = new ConcurrentDictionary<int, List<IAGV>>();
        internal static event EventHandler<int> OnPtPassableBecausePartsReplaceFinish;
        private static Logger logger = LogManager.GetLogger("DispatchCenterLog");
        private static SemaphoreSlim RemoveOrAddWorkStationInPartsReplacingSemaphore = new SemaphoreSlim(1, 1);
        public static void Initialize()
        {
            TrafficDeadLockMonitor.StartAsync();
            VehicleNavigationState.OnAGVStartWaitConflicSolve += TrafficDeadLockMonitor.HandleVehicleStartWaitConflicSolve;
            VehicleNavigationState.OnAGVNoWaitConflicSolve += TrafficDeadLockMonitor.HandleVehicleNoWaitConflicSolve;

        }

        public static async Task<IEnumerable<MapPoint>> MoveToDestineDispatchRequest(IAGV vehicle, MapPoint startPoint, MapPoint goalPoint, clsTaskDto taskDto, VehicleMovementStage stage, GOAL_SELECT_METHOD goalSelectMethod = GOAL_SELECT_METHOD.TO_POINT_INFRONT_OF_GOAL, bool isNavigationLostTimeout = false)
        {
            bool _waitTrafficHandleSignalTimeout = true;
            try
            {
                _waitTrafficHandleSignalTimeout = !await TrafficControlCenter._leaveWorkStaitonReqSemaphore.WaitAsync(TimeSpan.FromSeconds(2));
                if (_waitTrafficHandleSignalTimeout)
                {
                    vehicle.CurrentRunningTask().UpdateStateDisplayMessage($"等待交管訊號逾時");
                    await Task.Delay(1000);
                    return null;
                }
                await semaphore.WaitAsync();
                MapPoint finalMapPoint = goalPoint;
                var path = await GenNextNavigationPath(vehicle, startPoint, finalMapPoint, taskDto, stage, goalSelectMethod, isNavigationLostTimeout);
                if (path == null)
                    return null;
                path = GetPathWithDestineWorkStationStatusCheck(vehicle, path);
                if (path == null)
                    return null;

                //if (!TrafficControlCenter.TrafficControlParameters.Experimental.NearRackPortParkable)
                //{
                //    path = GetPathWithDestineWorkStationNearbyStatucCheck(vehicle, path);
                //    if (path == null)
                //        return null;
                //}

                path = taskDto.IsHighestPriorityTask ? path : GetPathNavToTrafficCheckPoint(vehicle, path);
                if (path == null)
                    return null;
                path = GetPathWithPathReverseCheck(vehicle, startPoint, path);
                if (path == null)
                    return null;
                return path.Clone();
            }
            catch (RotatingOnSpinForbidPtException ex)
            {
                throw ex;
            }
            catch (NoPathForNavigatorException ex)
            {
                logger.Error(ex);
                throw ex;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
            finally
            {
                if (!_waitTrafficHandleSignalTimeout)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        TrafficControlCenter._leaveWorkStaitonReqSemaphore.Release();
                    });
                }
                await Task.Delay(10);
                semaphore.Release();
            }
        }


        private static IEnumerable<MapPoint>? GetPathWithDestineWorkStationNearbyStatucCheck(IAGV vehicle, IEnumerable<MapPoint>? path)
        {
            IEnumerable<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            //判斷目的地包含有工作站之鄰近點是否有車
            MapPoint workStationPt = vehicle.GetNextWorkStationTag().GetMapPoint();

            //若目的地是一般點位，檢查當前路徑是否已包含目的地
            if (workStationPt.StationType == MapPoint.STATION_TYPE.Normal && !path.Any(pt => pt.TagNumber == workStationPt.TagNumber))
                return path;

            //若目的地是工作站點，檢查當前路徑是否已包含工作站進入點
            if (workStationPt.StationType != MapPoint.STATION_TYPE.Normal && !workStationPt.TargetNormalPoints().Any(pt => path.GetTagCollection().Contains(pt.TagNumber)))
                return path;
            List<MapPoint> nearbyWorkStations = workStationPt.GetNearByWorkStationAndEntryPoint();
            if (!nearbyWorkStations.Any())
                return path;

            List<MapPoint> ptToCheckVehicleExist = new List<MapPoint>();
            ptToCheckVehicleExist.AddRange(nearbyWorkStations);
            foreach (var item in nearbyWorkStations)
                ptToCheckVehicleExist.AddRange(item.TargetNormalPoints());
            if (ptToCheckVehicleExist.Any(pt => otherVehicles.Any(v => v.currentMapPoint.TagNumber == pt.TagNumber)))
            {
                vehicle.CurrentRunningTask().UpdateStateDisplayMessage($"鄰近點不可停車，所以需等待其他車輛離開...");
                return null;
            }
            return path;

        }

        private static IEnumerable<MapPoint>? GetPathWithDestineWorkStationStatusCheck(IAGV vehicle, IEnumerable<MapPoint>? path)
        {
            if (path == null)
                return null;

            IEnumerable<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            //判斷是否有其他車輛當前位置位於目的地工作站內且當前路徑包含工作站進入點或進入點前
            int currentWorkStationTagToGo = vehicle.GetNextWorkStationTag();
            bool isDestineBelongEntryPointsOfWorkStations = currentWorkStationTagToGo.IsEntryPointOfWorkStation(out IEnumerable<MapPoint> workStations);


            List<int> constrainTags = new List<int>();
            IAGV vehicleAtWorkStation = null;

            if (isDestineBelongEntryPointsOfWorkStations)
            {
                vehicleAtWorkStation = otherVehicles.FirstOrDefault(_vehicle => workStations.Select(pt => pt.TagNumber).Contains(_vehicle.currentMapPoint.TagNumber));
                if (vehicleAtWorkStation != null && isRotationWillConflicToVehicleAtWorkStation(vehicle, vehicleAtWorkStation))
                    constrainTags.Add(currentWorkStationTagToGo);

                bool isRotationWillConflicToVehicleAtWorkStation(IAGV agvGoTo, IAGV atWorkStationAGV)
                {
                    MapPoint toGoMapPoint = StaMap.GetPointByTagNumber(currentWorkStationTagToGo);
                    var agvRotationGeo = agvGoTo.AGVRotaionGeometry.Clone();
                    agvRotationGeo.SetCenter(toGoMapPoint.X, toGoMapPoint.Y);
                    //要假裝AGV已達終點
                    return agvRotationGeo.IsIntersectionTo(atWorkStationAGV.AGVRotaionGeometry);
                }
            }
            else
            {
                IEnumerable<MapPoint> entryPoints = StaMap.GetPointByTagNumber(currentWorkStationTagToGo).TargetNormalPoints();
                constrainTags.AddRange(entryPoints.GetTagCollection());
                constrainTags.AddRange(entryPoints.SelectMany(entryPt => entryPt.TargetNormalPoints().GetTagCollection()));
                vehicleAtWorkStation = otherVehicles.FirstOrDefault(_vehicle => _vehicle.currentMapPoint.TagNumber == currentWorkStationTagToGo);
            }
            if (vehicleAtWorkStation != null && path.GetTagCollection().Intersect(constrainTags).Any())
            {
                vehicle.CurrentRunningTask().UpdateStateDisplayMessage($"等待位於目的地-{currentWorkStationTagToGo.GetDisplayAtCurrentMap()} 車輛({vehicleAtWorkStation.Name})離開...");
                return null;
            }

            return path;
        }

        private static IEnumerable<MapPoint>? GetPathWithPathReverseCheck(IAGV vehicle, MapPoint startPoint, IEnumerable<MapPoint>? path)
        {
            if (path != null && path.Count() > 1 && startPoint.SpinMode == 1)
            {
                List<MapPoint> _pathDetecting = path.ToList();
                //檢查是否當前位置是否為不可旋轉點且轉向下一個目標需旋轉
                double agvCurrentAngle = vehicle.states.Coordination.Theta;
                //行駛至下一個點的朝向角
                double forwardAngleToNextPoint = Tools.CalculationForwardAngle(_pathDetecting[0], _pathDetecting[1]);
                double angleToTurn = Tools.CalculateTheateDiff(agvCurrentAngle, forwardAngleToNextPoint);
                if (angleToTurn > 45)
                {
                    vehicle.NavigationState.CurrentConflicRegion = new AGVSystemCommonNet6.MAP.Geometry.MapRectangle()
                    {
                        StartPoint = _pathDetecting[0],
                        EndPoint = _pathDetecting[1]
                    };
                    throw new RotatingOnSpinForbidPtException();
                }
                else
                    return path;
            }
            else
                return path;
        }

        private static IEnumerable<MapPoint>? GetPathNavToTrafficCheckPoint(IAGV vehicle, IEnumerable<MapPoint>? path)
        {
            if (path != null && path.Count() != 0 && TrafficControlCenter.TrafficControlParameters.Experimental.NavigationWithTrafficControlPoints)
            {
                var _path = path.Clone().ToList();
                var firstTrafficControlPt = _path.Skip(1).FirstOrDefault(pt => pt.IsTrafficCheckPoint);
                if (firstTrafficControlPt != null)
                {
                    NotifyServiceHelper.INFO($"{vehicle.Name} Go to traffic check point [{firstTrafficControlPt.TagNumber}] now!");
                    int index = _path.IndexOf(firstTrafficControlPt); // 0,1,2,3,4
                    path = _path.Take(index + 1);
                }
            }

            return path;
        }

        private static async Task<IEnumerable<MapPoint>> GenNextNavigationPath(IAGV vehicle, MapPoint startPoint, MapPoint goalPoint, clsTaskDto order, VehicleMovementStage stage, GOAL_SELECT_METHOD goalSelectMethod = GOAL_SELECT_METHOD.TO_POINT_INFRONT_OF_GOAL, bool isNavigationLostTimeout = false)
        {
            vehicle.NavigationState.ResetNavigationPointsOfPathCalculation();
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            MapPoint finalMapPoint = goalPoint;
            IEnumerable<MapPoint> optimizePath_Init_No_constrain = null;

            optimizePath_Init_No_constrain = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, GetConstrainsOfPointsOnlyUseForSpeficTagsInRegions(vehicle, isNavigationLostTimeout), vehicle.states.Coordination.Theta);

            IEnumerable<MapPoint> optimizePath_Init = null;

            try
            {
                if (order.IsHighestPriorityTask)
                    optimizePath_Init = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, GetConstrainsOfHighestPriorityOdrder(vehicle, otherAGV, finalMapPoint, ref optimizePath_Init_No_constrain), vehicle.states.Coordination.Theta);
                else
                    optimizePath_Init = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, GetConstrains(vehicle, otherAGV, finalMapPoint, isNavigationLostTimeout), vehicle.states.Coordination.Theta);
            }
            catch (Exception ex)
            {
                optimizePath_Init = optimizePath_Init_No_constrain;
            }

            Dictionary<MapPoint, bool> wayStates = optimizePath_Init.ToDictionary(pt => pt, pt => pt.IsSingleWay());
            IEnumerable<MapPoint> optimizePathFound = null;
            MapRegion vehicleCurrentRegion = vehicle.currentMapPoint.GetRegion();
            MapRegion finalPointRegion = finalMapPoint.GetRegion();

            var outPtOfSingleWay = optimizePath_Init.GetOutPointOfPathWithSingleWay();

            bool _isInNarrowRegion = vehicleCurrentRegion.IsNarrowPath;
            vehicle.NavigationState.UpdateNavigationPointsForPathCalculation(optimizePath_Init);
            var usableSubGoals = optimizePath_Init.Skip(0).Where(pt => pt.CalculateDistance(vehicle.currentMapPoint) >= 1.5)
                                                          .Where(pt => !pt.IsVirtualPoint && !GetConstrains(vehicle, otherAGV, finalMapPoint, isNavigationLostTimeout).GetTagCollection().Contains(pt.TagNumber))
                                                          .Where(pt => otherAGV.All(agv => pt.CalculateDistance(agv.currentMapPoint) >= (_isInNarrowRegion ? 2 : 1.5)))
                                                          //.Where(pt => otherAGV.All(agv => !agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetCircleArea(ref vehicle, 0.2).IsIntersectionTo(vehicle.AGVRotaionGeometry))))
                                                          .ToList();
            if (outPtOfSingleWay != null)
            {
                usableSubGoals.Add(outPtOfSingleWay);
                usableSubGoals = usableSubGoals.DistinctBy(pt => pt.TagNumber).OrderBy(pt => pt.CalculateDistance(vehicle.currentMapPoint)).ToList();
            }

            usableSubGoals = usableSubGoals.Any() ? usableSubGoals : new List<MapPoint>() { finalMapPoint };
            List<IEnumerable<MapPoint>> subGoalResults = new List<IEnumerable<MapPoint>>();

            foreach (var point in usableSubGoals)
            {
                var goalIndex = optimizePath_Init.ToList().FindIndex(pt => pt.TagNumber == point.TagNumber);
                var optmizePath_init_sub = optimizePath_Init.Take(goalIndex + 1).ToList();
                vehicle.NavigationState.UpdateNavigationPointsForPathCalculation(optmizePath_init_sub);
                var result = await FindPath(vehicle, otherAGV, point, optmizePath_init_sub, false);
                subGoalResults.Add(result);
            }



            if (subGoalResults.Any() && !subGoalResults.All(path => path == null))
            {

                try
                {
                    List<MapPoint> path = new();

                    IEnumerable<MapPoint> _noConflicPathToDestine = subGoalResults.FirstOrDefault(_path => _path != null && _path.Last().TagNumber == finalMapPoint.TagNumber && _path.All(pt => !otherAGV.SelectMany(agv => agv.NavigationState.NextNavigtionPoints).GetTagCollection().Contains(pt.TagNumber)));

                    if (_noConflicPathToDestine != null && !_WillFinalStopPointConflicMaybe(_noConflicPathToDestine))
                    {
                        int pathLength = _noConflicPathToDestine.Count();
                        if (pathLength > 2)
                        {

                            Dictionary<MapPoint, MapRegion> stopablePointsAndRegion = _noConflicPathToDestine.Skip(1).Where(pt => !pt.IsVirtualPoint)
                                                                                                             .ToDictionary(pt => pt, pt => pt.GetRegion(StaMap.Map));
                            IEnumerable<string> regionNames = stopablePointsAndRegion.Values.Select(region => region.Name).Distinct();

                            var select = stopablePointsAndRegion.LastOrDefault(p => p.Value.Name == regionNames.First());
                            if (select.Key != null)
                            {
                                MapPoint ptToTempStop = select.Key;
                                int takeLen = _noConflicPathToDestine.ToList().IndexOf(ptToTempStop) + 1; //0 1 2
                                path = _noConflicPathToDestine.Take(takeLen).ToList(); //先移動到終點前一點(非虛擬點)
                            }
                            else
                            {
                                path = _noConflicPathToDestine.ToList();
                            }
                        }
                        else
                        {
                            path = _noConflicPathToDestine.ToList();
                        }


                    }
                    else
                    {

                        var pathCandicates = subGoalResults.Where(_p => _p != null);
                        if (goalSelectMethod == GOAL_SELECT_METHOD.TO_GOAL_DIRECTLY && pathCandicates.Any(path => path.Last().TagNumber == finalMapPoint.TagNumber))
                        {
                            return pathCandicates.First(path => path.Last().TagNumber == finalMapPoint.TagNumber);
                        }
                        if (pathCandicates.Count() > 2)
                        {
                            path = pathCandicates.ToList()[pathCandicates.Count() - 2].ToList();
                        }
                        else
                            path = subGoalResults.First(path => path != null).ToList();
                    }

                    if (path != null)
                    {
                        bool FinalStopPointConflicMaybe = _WillFinalStopPointConflicMaybe(path);
                        bool willConflicMaybe = _noConflicPathToDestine == null && FinalStopPointConflicMaybe;
                        return willConflicMaybe ? null : path;
                    }
                    return path;

                    #region local methods

                    bool _WillFinalStopPointConflicMaybe(IEnumerable<MapPoint> _path)
                    {
                        if (_path.Count() < 2)
                        {
                            return WillRotationAtCurrentPointConflicTo(vehicle, order, stage, _path, otherAGV);
                            //return;
                        }
                        bool isTooCloseWithVehicleEntryPointOFWorkStation = false;
                        IEnumerable<IAGV> atWorkStationVehicles = otherAGV.Where(agv => !agv.currentMapPoint.IsCharge && agv.currentMapPoint.StationType != MapPoint.STATION_TYPE.Normal);

                        try
                        {
                            isTooCloseWithVehicleEntryPointOFWorkStation = atWorkStationVehicles.Select(_vehicle => StaMap.GetPointByTagNumber(vehicle.CurrentRunningTask().TaskDonwloadToAGV.Homing_Trajectory.First().Point_ID))
                                                 .Any(point => point.GetCircleArea(ref vehicle, 1.8).IsIntersectionTo(vehicle.AGVRotaionGeometry));
                        }
                        catch (Exception)
                        {

                        }
                        var conflicVehicles = otherAGV.Where(_vehicle => !_vehicle.currentMapPoint.IsCharge && _vehicle.AGVRotaionGeometry.IsIntersectionTo(_path.Last().GetCircleArea(ref vehicle, 1.1)));
                        return isTooCloseWithVehicleEntryPointOFWorkStation || conflicVehicles.Any();
                    }

                    #endregion
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                    return null;
                }
            }
            else
            {
                vehicle.NavigationState.ResetNavigationPointsOfPathCalculation();
                return null;

                bool CalculateThetaError(double finalThetaCheck, out double error)
                {
                    double angleDifference = finalThetaCheck - vehicle.states.Coordination.Theta;
                    if (angleDifference > 180)
                        angleDifference -= 360;
                    else if (angleDifference < -180)
                        angleDifference += 360;
                    error = Math.Abs(angleDifference);
                    return error < 5;
                }
            }
            async Task<IEnumerable<MapPoint>> FindPath(IAGV vehicle, IEnumerable<IAGV> otherAGV, MapPoint _finalMapPoint, IEnumerable<MapPoint> oriOptimizePath, bool autoSolve = false)
            {
                IEnumerable<MapPoint> _optimizePath = null;
                MapRectangle conflicRegion = null;
                if (FindConflicRegion(vehicle, out conflicRegion))
                {
                    vehicle.NavigationState.CurrentConflicRegion = conflicRegion;
                }
                else
                {
                    vehicle.NavigationState.CurrentConflicRegion = null;
                    _optimizePath = oriOptimizePath;
                }
                return _optimizePath;

                bool FindConflicRegion(IAGV vehicle, out MapRectangle _conflicRegion)
                {
                    TaskBase? vehicleRunningTask = vehicle.CurrentRunningTask();
                    _conflicRegion = null;
                    List<IAGV> otherDispatingVehicle = otherAGV.ToList();
                    var _nextPath = vehicle.NavigationState.NextNavigtionPointsForPathCalculation;

                    if (_nextPath.DistinctBy(p => p.TagNumber).Count() == 1 && !WillRotationAtCurrentPointConflicTo(vehicle, order, stage, _nextPath, otherAGV)) //原地
                    {
                        return false;
                    }
                    var _PathRectangles = vehicle.NavigationState.NextPathOccupyRegionsForPathCalculation;
                    //_PathRectangles.Reverse();
                    foreach (var item in _PathRectangles)
                    {
                        bool isConflic = false;
                        foreach (var _otherAGV in otherDispatingVehicle)
                        {
                            bool _isAtChargeStation = _otherAGV.currentMapPoint.IsCharge;
                            bool _geometryConflic = item.IsIntersectionTo(_otherAGV.AGVRealTimeGeometery);
                            bool _pathConflic = _otherAGV.NavigationState.NextPathOccupyRegions.Any(reg => reg.IsIntersectionTo(item));
                            isConflic = _pathConflic || _geometryConflic;
                            MoveTaskDynamicPathPlanV2? _otherAGVRunningTask = null;
                            try
                            {
                                _otherAGVRunningTask = (_otherAGV.CurrentRunningTask() as MoveTaskDynamicPathPlanV2);
                            }
                            catch (Exception)
                            {
                            }
                            if (isConflic)
                            {
                                bool _isPointConflic = item.StartPoint.TagNumber == item.EndPoint.TagNumber;
                                vehicle.NavigationState.currentConflicToAGV = _otherAGV;
                                string des = $"路線與其他車體干涉 :{_geometryConflic} |路線衝突干涉 :{_pathConflic}";
                                if (_isPointConflic)
                                    vehicleRunningTask?.UpdateMoveStateMessage($"Point {item.StartPoint.TagNumber} Conflic To {_otherAGV.Name}\r\n({des})");
                                else
                                    vehicleRunningTask?.UpdateMoveStateMessage($"Path From {item.StartPoint.TagNumber} To {item.EndPoint.TagNumber} Conflic To {_otherAGV.Name}\r\n({des})");
                                break;
                            }
                        }
                        if (isConflic)
                        {
                            _conflicRegion = item;
                            break;
                        }
                        else
                        {
                            //vehicleRunningTask.IsWaitingSomeone = false;
                        }
                    }
                    return _conflicRegion != null;
                }
            }

        }
        private static bool WillRotationAtCurrentPointConflicTo(IAGV vehicle, clsTaskDto order, VehicleMovementStage stage, IEnumerable<MapPoint> _path, IEnumerable<IAGV> otherAGV)
        {
            //考慮原地旋轉是否會與其他車輛干涉
            var directionAngleFInal = _path.GetStopDirectionAngle(order, vehicle, stage, _path.Last());
            var rotationDiff = Math.Abs(vehicle.states.Coordination.Theta - directionAngleFInal);
            bool _rotationWillConflicToOtherVehiclePath = otherAGV.Any(v => v.NavigationState.NextPathOccupyRegions.Any(r => r.IsIntersectionTo(vehicle.AGVRotaionGeometry)));
            bool _rotationWillConflicToOtherVehicleCurrentBody = otherAGV.Any(v => v.AGVRotaionGeometry.IsIntersectionTo(vehicle.AGVRotaionGeometry));
            return rotationDiff > 10 && (_rotationWillConflicToOtherVehiclePath || _rotationWillConflicToOtherVehicleCurrentBody);
        }

        public static List<MapPoint> GetConstrains(IAGV MainVehicle, IEnumerable<IAGV>? otherAGV, MapPoint finalMapPoint, bool isNavigationLostTimeout = false)
        {
            List<MapPoint> constrains = new List<MapPoint>();

            // 预先缓存注册的点位
            var registedPoints = StaMap.RegistDictionary
                .Where(pari => pari.Value.RegisterAGVName != MainVehicle.Name)
                .Select(p => StaMap.GetPointByTagNumber(p.Key))
                .ToList();
            constrains.AddRange(registedPoints);

            // 优化获取已进入的点
            IEnumerable<MapPoint> GetVehicleEnteredEntryPoint(IAGV _vehicle)
            {
                if (!_vehicle.currentMapPoint.IsCharge)
                    return Enumerable.Empty<MapPoint>();

                return _vehicle.currentMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            }

            IEnumerable<MapPoint> GetOtherVehicleChargeStationEnteredEntryPoint(IAGV _vehicle)
            {
                if (!_vehicle.currentMapPoint.IsCharge)
                    return Enumerable.Empty<MapPoint>();

                return _vehicle.currentMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
            }

            // 优化获取车辆重叠点
            IEnumerable<MapPoint> GetVehicleOverlapPoint(IAGV _vehicle)
            {
                return StaMap.Map.Points.Values
                    .Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal)
                    .Where(pt => pt.CalculateDistance(_vehicle.states.Coordination) <= _vehicle.AGVRotaionGeometry.RotationRadius);
            }

            var disabledPoints = StaMap.Map.Points.Values
                .Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && !pt.Enable)
                .ToList();

            if (TrafficControlCenter.TrafficControlParameters.DisableChargeStationEntryPointWhenNavigation)
            {
                constrains.AddRange(otherAGV.SelectMany(_vehicle => GetOtherVehicleChargeStationEnteredEntryPoint(_vehicle))); // 充电站进入点不可用
            }

            constrains.AddRange(otherAGV.SelectMany(_vehicle => _vehicle.NavigationState.NextNavigtionPoints)); // 其他车的导航路径不可用

            // 优化：添加重叠点
            constrains.AddRange(otherAGV.Where(v => v.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None)
                                         .Where(v => v.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal)
                                         .SelectMany(_vehicle => GetVehicleOverlapPoint(_vehicle)));

            constrains.AddRange(disabledPoints); // 图纸中不可用的点

            // 去重：使用字典来避免使用 DistinctBy
            var uniqueConstrains = new Dictionary<int, MapPoint>();
            foreach (var point in constrains)
            {
                uniqueConstrains[point.TagNumber] = point;
            }

            // 获取附加的注册点
            List<MapPoint> additionRegists = uniqueConstrains.Values
                .SelectMany(pt => pt.RegistsPointIndexs.Select(_index => StaMap.GetPointByIndex(_index)))
                .ToList();
            additionRegists.ForEach(regPoint => uniqueConstrains[regPoint.TagNumber] = regPoint);

            // 加入特定标签区域的限制
            var additionalConstrains = GetConstrainsOfPointsOnlyUseForSpeficTagsInRegions(MainVehicle, isNavigationLostTimeout);
            foreach (var pt in additionalConstrains)
            {
                uniqueConstrains[pt.TagNumber] = pt;
            }

            // 如果有冲突区域，加入结束点
            if (MainVehicle.NavigationState.CurrentConflicRegion != null)
            {
                var conflictEndPoint = MainVehicle.NavigationState.CurrentConflicRegion.EndPoint;
                if (conflictEndPoint.TagNumber != MainVehicle.NavigationState.CurrentConflicRegion.StartPoint.TagNumber)
                {
                    uniqueConstrains[conflictEndPoint.TagNumber] = conflictEndPoint;
                }
            }

            // 加入最后等待通过超时的点
            if (MainVehicle.NavigationState.LastWaitingForPassableTimeoutPt != null)
            {
                uniqueConstrains[MainVehicle.NavigationState.LastWaitingForPassableTimeoutPt.TagNumber] = MainVehicle.NavigationState.LastWaitingForPassableTimeoutPt;
            }

            // 返回去重后的结果
            return uniqueConstrains.Values.ToList();
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="agv"></param>
        /// <param name="isNavigationLostTimeout"></param>
        /// <returns></returns>
        private static List<MapPoint> GetConstrainsOfPointsOnlyUseForSpeficTagsInRegions(IAGV agv, bool isNavigationLostTimeout = false)
        {
            int nextWorkStationTag = agv.GetNextWorkStationTag();
            MapRegion agvCurrentRegion = agv.currentMapPoint.GetRegion();
            List<MapRegion> regionsExcludingCurrentRegion = StaMap.Map.Regions.Where(region => region.RegionType != MapRegion.MAP_REGION_TYPE.UNKNOWN && region.Name != agvCurrentRegion.Name && region.PathOnlyUseForTagsWhenVehicleFromOutsideRegion.Any()).ToList();
            if (isNavigationLostTimeout)
            {
                bool anyRegionCanOpenPathForPoorAgv = regionsExcludingCurrentRegion.Any(region => region.IsPathCanUseWhenAgvNoPathToUse && region.PathOnlyUseForTagsWhenVehicleFromOutsideRegion.Any());
                if (anyRegionCanOpenPathForPoorAgv)
                {
                    regionsExcludingCurrentRegion = regionsExcludingCurrentRegion.Where(region => !region.IsPathCanUseWhenAgvNoPathToUse).ToList();
                    agv.CurrentRunningTask()?.LogInfoAsync($"因為找不到路徑持續了一段持間, 路徑規劃中心開放了一些原本僅能提供部分終點使用的區域", true);
                }
            }
            List<int> acceptGoalTags = regionsExcludingCurrentRegion.SelectMany(region => region.PathOnlyUseForTagsWhenVehicleFromOutsideRegion).ToList();
            if (acceptGoalTags.Contains(nextWorkStationTag))
                return new List<MapPoint>();
            var result = regionsExcludingCurrentRegion.SelectMany(region => region.GetPointsInRegion()).ToList();
            return result;
        }

        private static List<MapPoint> GetConstrainsOfHighestPriorityOdrder(IAGV MainVehicle, IEnumerable<IAGV>? otherAGV, MapPoint finalMapPoint, ref IEnumerable<MapPoint> optimizePath_Init_No_constrain)
        {
            List<int> tagsOfTopPriorityOrderPath = optimizePath_Init_No_constrain.GetTagCollection().ToList();
            List<MapPoint> exceptPoints = StaMap.Map.Points.Values.Where(pt => !tagsOfTopPriorityOrderPath.Contains(pt.TagNumber)).ToList();
            return exceptPoints;

        }
        internal static async Task SyncTrafficStateFromAGVSystemInvoke()
        {
            try
            {
                List<int> partsReplacingEqTags = await AGVSSerivces.TRAFFICS.GetTagsOfEQPartsReplacing();

                if (!partsReplacingEqTags.Any())//沒有任何設備在進行零件更換
                {
                    logger.Info($"There is not any equipment is in parts replacing state!");
                    List<int> finishedPartsReplacingEqTags = TagListOfWorkstationInPartsReplacing.ToList();
                    foreach (var tag in finishedPartsReplacingEqTags)
                    {
                        await RemoveWorkStationInPartsReplacing(tag);
                    }
                    TagListOfWorkstationInPartsReplacing.Clear();
                    TagListOfInFrontOfPartsReplacingWorkstation.Clear();
                }
                else
                {
                    //過濾出已經完成零件更換的設備TAG

                    List<int> finishedPartsReplacingEqTags = TagListOfWorkstationInPartsReplacing.Where(tag => !partsReplacingEqTags.Contains(tag)).ToList();
                    foreach (var tag in finishedPartsReplacingEqTags)
                    {
                        await RemoveWorkStationInPartsReplacing(tag);
                    }
                }

                //過濾出新加入的設備TAG
                partsReplacingEqTags = partsReplacingEqTags.Where(tag => !TagListOfWorkstationInPartsReplacing.Contains(tag)).ToList();
                foreach (var tag in partsReplacingEqTags)
                {
                    await AddWorkStationInPartsReplacing(tag);
                }
                logger.Trace($"SyncTrafficStateFromAGVSystemInvoke done");
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }
        internal static async Task AddWorkStationInPartsReplacing(int workstationTag)
        {
            try
            {
                await RemoveOrAddWorkStationInPartsReplacingSemaphore.WaitAsync();
                MapPoint eqPoint = StaMap.GetPointByTagNumber(workstationTag);
                if (!TagListOfWorkstationInPartsReplacing.Contains(workstationTag))
                {
                    TagListOfWorkstationInPartsReplacing.Add(workstationTag);
                    TagListOfInFrontOfPartsReplacingWorkstation.AddRange(eqPoint.TargetNormalPoints().GetTagCollection());
                    NotifyTagsNotPassable();
                }

                //條件:任務執行中且正在執行移動任務中，並且路徑包含到不可通行的點
                var cycleStopVehicles = VMSManager.AllAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                                                         .Where(agv => agv.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None)
                                                         .Where(agv => agv.NavigationState.NextNavigtionPoints.Skip(1).ToList().Any(pt => TagListOfInFrontOfPartsReplacingWorkstation.Contains(pt.TagNumber)))
                                                         .ToList();

                if (cycleStopVehicles.Any())
                {
                    var frontPoints = StaMap.GetPointByTagNumber(workstationTag).TargetNormalPoints();
                    var blockedTag = frontPoints.First().TagNumber;
                    MapPoint blockedMapPoint = StaMap.GetPointByTagNumber(blockedTag);
                    foreach (var _vehicle in cycleStopVehicles)
                    {
                        if (!AGVNavigationPauseStore.ContainsKey(workstationTag))
                        {
                            AGVNavigationPauseStore.TryAdd(workstationTag, new List<IAGV>());
                        }
                        AGVNavigationPauseStore[workstationTag].Add(_vehicle);

                        _ = Task.Run(async () =>
                        {
                            _vehicle.CurrentRunningTask().NavigationPause(isPauseWhenNavigating: true, $"Wait EQ({eqPoint.Graph.Display}) Parts Replacing Finish", blockedMapPoint);
                            _vehicle.CurrentRunningTask().CycleStopRequestAsync();
                        });

                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                RemoveOrAddWorkStationInPartsReplacingSemaphore.Release();
            }


        }

        internal static async Task RemoveWorkStationInPartsReplacing(int workstationTag)
        {
            try
            {
                await RemoveOrAddWorkStationInPartsReplacingSemaphore.WaitAsync();

                if (TagListOfWorkstationInPartsReplacing.Contains(workstationTag))
                {
                    TagListOfWorkstationInPartsReplacing.Remove(workstationTag);
                }

                MapPoint eqPoint = StaMap.GetPointByTagNumber(workstationTag);
                List<int> tagsOfEqEntry = eqPoint.TargetNormalPoints().GetTagCollection().ToList();

                foreach (var tag in tagsOfEqEntry)
                {
                    OnPtPassableBecausePartsReplaceFinish?.Invoke("", tag);
                }

                TagListOfInFrontOfPartsReplacingWorkstation = TagListOfInFrontOfPartsReplacingWorkstation.Where(tag => !tagsOfEqEntry.Contains(tag)).ToList();
                NotifyTagsNotPassable();

                if (AGVNavigationPauseStore.TryGetValue(workstationTag, out List<IAGV> navigationPausingAgvList))
                {
                    foreach (var agv in navigationPausingAgvList)
                    {
                        agv.CurrentRunningTask().NavigationResume(false);
                    }
                    AGVNavigationPauseStore.TryRemove(workstationTag, out _);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                RemoveOrAddWorkStationInPartsReplacingSemaphore.Release();
            }
        }

        private static void NotifyTagsNotPassable()
        {
            if (TagListOfInFrontOfPartsReplacingWorkstation.Any())
            {
                string tagsDisplayOfClosedByEqPartsReplace = string.Join(",", TagListOfInFrontOfPartsReplacingWorkstation);
                string logmsg = $"當前因設備零件更換而封閉Tag={tagsDisplayOfClosedByEqPartsReplace}";
                logger.Trace(logmsg);
                NotifyServiceHelper.WARNING(logmsg);
            }
            else
            {
                string logmsg = $"目前沒有因設備零件更換而封閉的Tag";
                logger.Trace(logmsg);
                NotifyServiceHelper.SUCCESS(logmsg);
            }
        }

    }

    public static class Extensions
    {
        public static (int num, IEnumerable<MapPoint> registedPoints) GetRegistPoints(this clsPathInfo pathInfo, IAGV PathOwner = null)
        {
            if (!pathInfo.stations.Any())
                return (0, null);


            var registedPt = pathInfo.stations.Where(pt => StaMap.RegistDictionary.Keys.Contains(pt.TagNumber))
                                              .Where(pt => PathOwner == null ? true : StaMap.RegistDictionary[pt.TagNumber].RegisterAGVName != PathOwner.Name);


            return (registedPt.Count(), registedPt);

        }
        public static int GetRegistPointsNum(this clsPathInfo pathInfo)
        {
            if (!pathInfo.stations.Any())
                return 0;

            var registedPt = pathInfo.stations.Where(pt => StaMap.RegistDictionary.Keys.Contains(pt.TagNumber));

            return registedPt.Count();

        }
    }

}

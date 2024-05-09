using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.GPMRosMessageNet.Services;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using Microsoft.AspNetCore.Localization;
using RosSharp.RosBridgeClient.MessageTypes.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection.Metadata.Ecma335;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static AGVSystemCommonNet6.Vehicle_Control.CarComponent;
using static VMSystem.TrafficControl.VehicleNavigationState;

namespace VMSystem.Dispatch
{
    public static class DispatchCenter
    {
        private static Map map => StaMap.Map;
        internal static List<int> TagListOfWorkstationInPartsReplacing { get; private set; } = new List<int>();
        internal static event EventHandler<int> OnWorkStationStartPartsReplace;
        internal static event EventHandler<int> OnWorkStationFinishPartsReplace;
        internal static List<int> TagListOfInFrontOfPartsReplacingWorkstation
        {
            get
            {
                lock (TagListOfWorkstationInPartsReplacing)
                {
                    return TagListOfWorkstationInPartsReplacing.SelectMany(tag =>
                                                StaMap.GetPointByTagNumber(tag).Target.Keys.Select(index => StaMap.GetPointByIndex(index).TagNumber)).ToList();
                }
            }
        }

        public static Scheduler PathScheduler = new Scheduler();
        public static Dictionary<IAGV, PathPlanner> VehicleOrderStore { get; set; } = new Dictionary<IAGV, PathPlanner>();

        public static Dictionary<IAGV, clsVehicleNavigationController> CurrentNavingPathes = new Dictionary<IAGV, clsVehicleNavigationController>();



        public static List<IAGV> DispatingVehicles = new List<IAGV>();

        public static void Initialize()
        {
            CurrentNavingPathes = VMSManager.AllAGV.ToDictionary(vehicle => vehicle, vehicle => new clsVehicleNavigationController(vehicle));
        }

        public static async Task<IEnumerable<MapPoint>> MoveToGoalGetPath(IAGV requestVehicle, MapPoint startPoint, clsTaskDto taskDto, VehicleMovementStage stage)
        {
            clsVehicleNavigationController NavingController = CurrentNavingPathes[requestVehicle];
            MapPoint finalGoal = taskDto.GetFinalMapPoint(requestVehicle, stage);
            PathFinder finder = new PathFinder();
            List<clsPathInfo> pathes = finder.FindPathes(map, startPoint, finalGoal, new PathFinder.PathFinderOption
            {
                OnlyNormalPoint = true,
                Strategy = PathFinder.PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE
            });

            var orderedPathesInfo = pathes.OrderBy(path => path.total_rotation_angle)
                                          .OrderBy(path => path.GetRegistPointsNum()).ToList();

            NavingController.UpdateNavagionPlan(orderedPathesInfo.First().stations.ToList());

            var pathInfoFind = orderedPathesInfo.FirstOrDefault();

            await Task.Delay(500);

            var conflics = NavingController.GetConflics();

            if (conflics != null && conflics.Values.Any())
            {
                return NavingController.GetNextPath();
                //while (true)
                //{
                //    await Task.Delay(1000);
                //}
                return NavingController.currentNavigationPlan.ToList();
            }
            else
            {
                return NavingController.currentNavigationPlan.ToList();
            }

            IEnumerable<IAGV> _OthersVehicles()
            {
                return VMSManager.AllAGV.FilterOutAGVFromCollection(requestVehicle);
            }

            bool _IsPathContainChargeStationOfVehicleChargingOutPoint(clsPathInfo _pathInfo)
            {
                if (!_OthersVehicles().Any(_vehicle => _vehicle.currentMapPoint.IsCharge))
                    return false;

                var outpointsOfChargeStation = _OthersVehicles().SelectMany(_vehicle => _vehicle.currentMapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index)));
                return _pathInfo.tags.Intersect(outpointsOfChargeStation.GetTagCollection()).Count() != 0;
            }
        }
        public static async Task<IEnumerable<MapPoint>> MoveToDestineDispatchRequest(IAGV vehicle, MapPoint startPoint, clsTaskDto taskDto, VehicleMovementStage stage)
        {

            if (!DispatingVehicles.Contains(vehicle))
                DispatingVehicles.Add(vehicle);
            MapPoint finalMapPoint = taskDto.GetFinalMapPoint(vehicle, stage);
            RegionManager.RegistRegionToGo(vehicle, finalMapPoint);

            MapRegion finalMapRegion = finalMapPoint.GetRegion(CurrentMap);
            //if (finalMapRegion.EnteryTags.Any(tag => vehicle.currentMapPoint.TagNumber == tag))
            //{
            //    return await RegionControl(vehicle, vehicle.NavigationState.NextNavigtionPoints, true, finalMapRegion, finalMapPoint);
            //}
            var path = await GenNextNavigationPath(vehicle, startPoint, taskDto, stage);
            return path = path == null ? path : path.Clone();

        }
        private static async Task<IEnumerable<MapPoint>> GenNextNavigationPath(IAGV vehicle, MapPoint startPoint, clsTaskDto order, VehicleMovementStage stage)
        {
            vehicle.NavigationState.ResetNavigationPoints();

            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            MapPoint finalMapPoint = order.GetFinalMapPoint(vehicle, stage);
            IEnumerable<MapPoint> optimizePath_Init_No_constrain = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, new List<MapPoint>());
            IEnumerable<MapPoint> optimizePath_Init = null;

            try
            {
                optimizePath_Init = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, GetConstrains());
            }
            catch (Exception)
            {
                optimizePath_Init = optimizePath_Init_No_constrain;
            }


            IEnumerable<MapPoint> optimizePathFound = null;
            MapRegion vehicleCurrentRegion = vehicle.currentMapPoint.GetRegion(CurrentMap);
            MapRegion finalPointRegion = finalMapPoint.GetRegion(CurrentMap);

            bool _isInNarrowRegion = vehicleCurrentRegion.IsNarrowPath;

            vehicle.NavigationState.UpdateNavigationPoints(optimizePath_Init);
            var usableSubGoals = optimizePath_Init.Skip(1).Where(pt => pt.CalculateDistance(vehicle.currentMapPoint) >= (_isInNarrowRegion ? 2.5 : 2.5))
                                                          .Where(pt => !pt.IsVirtualPoint && !GetConstrains().GetTagCollection().Contains(pt.TagNumber))
                                                          .Where(pt => otherAGV.All(agv => pt.CalculateDistance(agv.currentMapPoint) >= (_isInNarrowRegion ? 2 : 2.5)))
                                                          //.Where(pt => otherAGV.All(agv => !agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetCircleArea(ref vehicle, 0.2).IsIntersectionTo(vehicle.AGVRotaionGeometry))))
                                                          .ToList();

            bool _isAnyVehicleInWorkStationOfNarrowRegion(MapRegion refRegion)
            {
                if (!refRegion.IsNarrowPath)
                    return false;
                return false;
                List<MapPoint> registedWorkStationsInNarrowRegion = StaMap.RegistDictionary.Keys.Select(tag => StaMap.GetPointByTagNumber(tag))
                                            .Where(point => point.StationType != MapPoint.STATION_TYPE.Normal)
                                            .Where(point => point.GetRegion(CurrentMap).Name == refRegion.Name)
                                            .ToList();
                return registedWorkStationsInNarrowRegion.Any();
            }

            if (finalPointRegion.Name == vehicleCurrentRegion.Name && vehicleCurrentRegion.IsNarrowPath && _isAnyVehicleInWorkStationOfNarrowRegion(finalPointRegion))
            {
                usableSubGoals = new List<MapPoint>() { finalMapPoint };
            }

            usableSubGoals = usableSubGoals.Any() ? usableSubGoals : new List<MapPoint>() { finalMapPoint };
            List<IEnumerable<MapPoint>> subGoalResults = new List<IEnumerable<MapPoint>>();

            foreach (var point in usableSubGoals)
            {
                var goalIndex = optimizePath_Init.ToList().FindIndex(pt => pt.TagNumber == point.TagNumber);
                var optmizePath_init_sub = optimizePath_Init.Take(goalIndex + 1).ToList();
                vehicle.NavigationState.UpdateNavigationPoints(optmizePath_init_sub);
                var result = await FindPath(vehicle, otherAGV, point, optmizePath_init_sub, false);
                subGoalResults.Add(result);
            }

            if (subGoalResults.Any() && !subGoalResults.All(path => path == null))
            {
                try
                {
                    var path = subGoalResults.First(path => path != null);
                    if (vehicle.currentMapPoint.TagNumber != finalMapPoint.TagNumber && path.Last().TagNumber == finalMapPoint.TagNumber)//前往終點(如果已經在終點則不考慮)
                    {
                        var confliAGVList = otherAGV.Where(agv => agv.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal && agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetCircleArea(ref vehicle, 1.2).IsIntersectionTo(finalMapPoint.GetCircleArea(ref vehicle, 1.2))))
                                                    .ToList();
                        bool is_destine_conflic = confliAGVList.Any();

                        if (is_destine_conflic)
                        {
                            (vehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"終點與其他車輛衝突");
                            NavigationPriorityHelper priorityHelper = new NavigationPriorityHelper();
                            ConflicSolveResult solveResult = await priorityHelper.GetPriorityByBecauseDestineConflicAsync(vehicle, confliAGVList, finalMapPoint);

                            switch (solveResult.NextAction)
                            {
                                case ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT:
                                    return null;
                                case ConflicSolveResult.CONFLIC_ACTION.REPLAN:
                                    return null;
                                case ConflicSolveResult.CONFLIC_ACTION.ACCEPT_GO:
                                    return path;
                                default:
                                    break;
                            }
                        }
                        else
                            return path;
                    }
                    return path;
                    //if (vehicle.NavigationState.State != VehicleNavigationState.NAV_STATE.WAIT_REGION_ENTERABLE)
                    //{
                    //    var pathPlanViaWorkStationPartsReplace = await WorkStationPartsReplacingControl(vehicle, path);
                    //    if (pathPlanViaWorkStationPartsReplace != null &&
                    //        !pathPlanViaWorkStationPartsReplace.GetTagCollection().SequenceEqual(path.GetTagCollection()))
                    //    {
                    //        return pathPlanViaWorkStationPartsReplace;
                    //    }
                    //}

                    //bool _isNowAtEntryPointOfRegion = finalMapPoint.GetRegion(CurrentMap).EnteryTags.Any(tag => vehicle.currentMapPoint.TagNumber == tag);
                    //if (_isNowAtEntryPointOfRegion)
                    //{

                    //    return await RegionControl(vehicle, path, _isNowAtEntryPointOfRegion, finalMapPoint.GetRegion(CurrentMap), finalMapPoint);
                    //}
                    //else
                    //{
                    //    return await RegionControl(vehicle, path, false);
                    //}
                }
                catch (Exception ex)
                {
                    LOG.ERROR(ex);
                    return null;
                }
            }
            else
            {
                vehicle.NavigationState.ResetNavigationPoints();
                return null;

                if (optimizePath_Init.Count() >= 2 && !StaMap.RegistDictionary.ContainsKey(optimizePath_Init.ToList()[2].TagNumber))
                {
                    vehicle.NavigationState.UpdateNavigationPoints(optimizePath_Init.Take(2));

                }
                else
                {
                    vehicle.NavigationState.ResetNavigationPoints();
                }
                return null;
            }

            vehicle.NavigationState.UpdateNavigationPoints(optimizePath_Init);

            while ((optimizePathFound = await FindPath(vehicle, otherAGV, finalMapPoint, optimizePath_Init, true)) == null)
            {
                vehicle.NavigationState.ResetNavigationPoints();
                if (vehicle.CurrentRunningTask().IsTaskCanceled || vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                    throw new TaskCanceledException();
                await Task.Delay(1000);
                vehicle.CurrentRunningTask().TrafficWaitingState.SetDisplayMessage("Finding Path...");
            }

            return optimizePathFound;

            async Task<IEnumerable<MapPoint>> FindPath(IAGV vehicle, IEnumerable<IAGV> otherAGV, MapPoint _finalMapPoint, IEnumerable<MapPoint> oriOptimizePath, bool autoSolve = false)

            {
                IEnumerable<MapPoint> _optimizePath = null;
                List<IAGV> otherDispatingVehicle = DispatingVehicles.FilterOutAGVFromCollection(vehicle).ToList();
                if (otherDispatingVehicle.Count == 0)
                {
                    otherDispatingVehicle.AddRange(otherAGV);

                    foreach (var item in otherDispatingVehicle)
                    {
                        item.NavigationState.ResetNavigationPoints();
                    }
                }

                MapRectangle conflicRegion = null;
                if (FindConflicRegion(vehicle, otherDispatingVehicle, out conflicRegion))
                {
                    bool willConflicRegionReleaseFuture = false;
                    MapPoint conflicRegionStartPt = StaMap.GetPointByTagNumber(conflicRegion.StartPointTag.TagNumber);
                    MapPoint conflicRegionEndPt = StaMap.GetPointByTagNumber(conflicRegion.EndPointTag.TagNumber);
                    //var conflicRegionOwners = otherDispatingVehicle.Where(agv => agv.currentMapPoint == conflicRegionStartPt || agv.currentMapPoint == conflicRegionEndPt);

                    var conflicRegionOwners = otherDispatingVehicle.Where(agv => agv.NavigationState.OccupyRegions.Any(reg => reg.StartPointTag.TagNumber == conflicRegion.StartPointTag.TagNumber
                                                                            || reg.EndPointTag.TagNumber == conflicRegion.EndPointTag.TagNumber) || agv.AGVGeometery.IsIntersectionTo(conflicRegion));

                    if (conflicRegionOwners.Any() && autoSolve)
                    {
                        if (vehicle.CurrentRunningTask().IsTaskCanceled || vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                            throw new TaskCanceledException();
                        await Task.Delay(1000);
                        List<Task<bool>> _DispatchOtherAGVsTask = new List<Task<bool>>();
                        string msg = "";
                        List<MapPoint> usedMoveDestinePoints = new List<MapPoint>();
                        foreach (var _agv in conflicRegionOwners)
                        {
                            var orderState = _agv.taskDispatchModule.OrderExecuteState;
                            if (orderState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                //任務進行中
                                var currentTask = _agv.CurrentRunningTask();
                                _DispatchOtherAGVsTask.Add(WaitingAGVFinishOrder(_agv));
                                msg += $"- Wait {_agv.Name} Order done\r\n";
                            }
                            else
                            {
                                IAGV agvFuckupAway = _agv;
                                IEnumerable<MapPoint> moveAwayPath = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(_agv.currentMapPoint, _finalMapPoint, new List<MapPoint>());
                                bool narrowPathDirction = _agv.currentMapPoint.GetRegion(StaMap.Map).IsNarrowPath && oriOptimizePath.Any(pt => pt.TagNumber != _agv.currentMapPoint.TagNumber);
                                var usablePoints = StaMap.Map.Points.Values
                                                        .Where(pt => !pt.IsVirtualPoint && pt.StationType == MapPoint.STATION_TYPE.Normal)
                                                        .Where(pt => !pt.GetCircleArea(ref agvFuckupAway, 1.2).IsIntersectionTo(vehicle.AGVGeometery))
                                                        .Where(pt => !pt.GetCircleArea(ref agvFuckupAway, 1.2).IsIntersectionTo(oriOptimizePath.Last().GetCircleArea(ref vehicle, 1.2)))
                                                        .Where(pt => oriOptimizePath.Where(_p => _p.TagNumber == pt.TagNumber).Count() == 0)
                                                        .Where(pt => !StaMap.RegistDictionary.ContainsKey(pt.TagNumber))
                                                        .Where(pt => !usedMoveDestinePoints.Contains(pt))
                                                        .OrderBy(pt => pt.CalculateDistance(_agv.currentMapPoint));

                                //var moveDestine = narrowPathDirction ? _agv.currentMapPoint : usablePoints.FirstOrDefault();
                                var moveDestine = usablePoints.FirstOrDefault();

                                if (moveDestine != null)
                                {
                                    usedMoveDestinePoints.Add(moveDestine);
                                    _DispatchOtherAGVsTask.Add(MoveAGVFuckupAway(_agv, moveDestine));
                                    msg += $"- Wait {_agv.Name} Fuck up away to {moveDestine.TagNumber}\r\n";
                                }
                            }
                        }

                        vehicle.CurrentRunningTask().TrafficWaitingState.SetDisplayMessage($"{msg}");

                        if (_DispatchOtherAGVsTask.Any())
                        {
                            vehicle.NavigationState.ResetNavigationPoints();
                            vehicle.NavigationState.State = VehicleNavigationState.NAV_STATE.WAIT_SOLVING;
                        }

                        var results = await Task.WhenAll(_DispatchOtherAGVsTask);

                        if (results.Any(ret => ret == false))
                            return null;

                        await Task.Delay(1000);
                        _optimizePath = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, _finalMapPoint, otherAGV.Select(agv => agv.currentMapPoint));
                        vehicle.NavigationState.UpdateNavigationPoints(_optimizePath);

                        //local methods 
                        async Task<bool> WaitingAGVFinishOrder(IAGV agv)
                        {
                            while (agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                if (vehicle.CurrentRunningTask().IsTaskCanceled || vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                                    throw new TaskCanceledException();
                                await Task.Delay(100);
                                var _optimizePathTryGet = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, _finalMapPoint, GetConstrains());
                                var usableSubGoals = _optimizePathTryGet.Skip(1).Where(pt => !pt.IsVirtualPoint && !StaMap.RegistDictionary.ContainsKey(pt.TagNumber));
                                foreach (var _goal in usableSubGoals)
                                {
                                    var _optmizePath_init_sub = _optimizePathTryGet.Where(pt => _optimizePathTryGet.ToList().IndexOf(pt) <= _optimizePathTryGet.ToList().IndexOf(_goal));
                                    vehicle.NavigationState.UpdateNavigationPoints(_optmizePath_init_sub);
                                    if (!FindConflicRegion(vehicle, otherDispatingVehicle, out var _region))
                                    {
                                        _optimizePath = _optmizePath_init_sub;
                                        return true;
                                    }
                                }
                                vehicle.NavigationState.ResetNavigationPoints();

                            }
                            return false;
                        }

                        async Task<bool> MoveAGVFuckupAway(IAGV agv, MapPoint destinPoint)
                        {

                            using (var agvsDb = new AGVSDatabase())
                            {
                                agvsDb.tables.Tasks.Add(new clsTaskDto
                                {
                                    DesignatedAGVName = agv.Name,
                                    Action = AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None,
                                    To_Station = destinPoint.TagNumber + "",
                                    TaskName = $"TAF-{DateTime.Now.ToString("yyMMddHHmmssfff")}",
                                    RecieveTime = DateTime.Now,
                                    DispatcherName = "交管-移車"
                                });
                                await agvsDb.SaveChanges();
                            }
                            while (agv.main_state != clsEnums.MAIN_STATUS.RUN)
                            {
                                if (vehicle.CurrentRunningTask().IsTaskCanceled || vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                                    throw new TaskCanceledException();
                                await Task.Delay(1);

                            }
                            await Task.Delay(100);


                            while (agv.currentMapPoint.TagNumber != destinPoint.TagNumber
                                    || agv.main_state == clsEnums.MAIN_STATUS.RUN
                                    || agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                await Task.Delay(100);
                                if (vehicle.CurrentRunningTask().IsTaskCanceled || vehicle.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                                    throw new TaskCanceledException();


                                var _optimizePathTryGet = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, _finalMapPoint, GetConstrains());
                                var usableSubGoals = _optimizePathTryGet.Skip(1).Where(pt => !pt.IsVirtualPoint && !StaMap.RegistDictionary.ContainsKey(pt.TagNumber));
                                foreach (var _goal in usableSubGoals)
                                {
                                    var _optmizePath_init_sub = _optimizePathTryGet.Where(pt => _optimizePathTryGet.ToList().IndexOf(pt) <= _optimizePathTryGet.ToList().IndexOf(_goal));
                                    vehicle.NavigationState.UpdateNavigationPoints(_optmizePath_init_sub);
                                    if (!FindConflicRegion(vehicle, otherDispatingVehicle, out var _region))
                                    {
                                        _optimizePath = _optmizePath_init_sub;
                                        return true;
                                    }
                                }
                                vehicle.NavigationState.ResetNavigationPoints();

                            }
                            return true;
                        }
                        await Task.Delay(1000);
                    }
                    //while (otherDispatingVehicle.Where(agv => agv.NavigationState.OccupyRegions.Any(reg => reg.StartPointTag.TagNumber == conflicRegion.StartPointTag.TagNumber
                    //                                                        || reg.EndPointTag.TagNumber == conflicRegion.EndPointTag.TagNumber)).Any())
                    //{


                    //}
                }
                else
                    _optimizePath = oriOptimizePath;
                return _optimizePath;

                static bool FindConflicRegion(IAGV vehicle, List<IAGV> otherDispatingVehicle, out MapRectangle _conflicRegion)
                {
                    _conflicRegion = null;
                    foreach (var item in vehicle.NavigationState.OccupyRegions)
                    {
                        bool isConflic = false;
                        foreach (var _otherAGV in otherDispatingVehicle)
                        {
                            bool _isAtChargeStation = _otherAGV.currentMapPoint.IsCharge;
                            bool _geometryConflic = item.IsIntersectionTo(_otherAGV.AGVGeometery);
                            bool _pathConflic = _otherAGV.NavigationState.OccupyRegions.Any(reg => reg.IsIntersectionTo(item));
                            isConflic = _pathConflic || _geometryConflic;
                            if (isConflic)
                            {
                                (vehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2)
                                    .UpdateMoveStateMessage($"{item.StartPointTag.TagNumber}-{item.EndPointTag.TagNumber} Conflic To {_otherAGV.Name}.\r\n(_geometryConflic:{_geometryConflic}/_pathConflic:{_pathConflic})");
                                break;
                            }
                        }
                        if (isConflic)
                        {
                            _conflicRegion = item;
                            break;
                        }
                    }

                    return _conflicRegion != null;
                }
            }

            List<MapPoint> GetConstrains()
            {
                List<MapPoint> constrains = new List<MapPoint>();
                var registedPoints = StaMap.RegistDictionary.Where(pari => pari.Value.RegisterAGVName != vehicle.Name).Select(p => StaMap.GetPointByTagNumber(p.Key));
                constrains.AddRange(registedPoints);

                IEnumerable<MapPoint> _GetVehicleEnteredEntryPoint(IAGV _vehicle)
                {
                    if (!_vehicle.currentMapPoint.IsCharge)
                        return new List<MapPoint>();

                    return _vehicle.currentMapPoint.Target.Keys
                                                    .Select(index => StaMap.GetPointByIndex(index));
                }

                IEnumerable<MapPoint> _GetVehicleOverlapPoint(IAGV _vehicle)
                {
                    return StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal)
                                                    .Where(pt => pt.GetCircleArea(ref vehicle, 0.5).IsIntersectionTo(_vehicle.AGVRotaionGeometry));
                }


                constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetVehicleEnteredEntryPoint(_vehicle)));
                constrains.AddRange(otherAGV.SelectMany(_vehicle => _vehicle.NavigationState.NextNavigtionPoints));
                constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetVehicleOverlapPoint(_vehicle)));
                var blockedTags = TryGetBlockedTagByEQMaintainFromAGVS().GetAwaiter().GetResult();
                constrains.AddRange(blockedTags.Select(tag => StaMap.GetPointByTagNumber(tag)));
                constrains = constrains.DistinctBy(st => st.TagNumber).ToList();
                constrains = constrains.Where(pt => pt.TagNumber != finalMapPoint.TagNumber).ToList();
                return constrains;
            }
        }
        private static async Task<IEnumerable<MapPoint>> WorkStationPartsReplacingControl(IAGV VehicleToEntry, IEnumerable<MapPoint> path)
        {
            List<int> _temporarilyClosedTags = TagListOfInFrontOfPartsReplacingWorkstation;
            List<int> _indexOfBlockedTag = _temporarilyClosedTags.Select(tag => path.ToList().FindIndex(pt => pt.TagNumber == tag)).ToList();

            if (_indexOfBlockedTag.All(index => index == -1))
            {
                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.RUNNING;
                return path;
            }

            _indexOfBlockedTag = _indexOfBlockedTag.OrderBy(_index => _index).ToList();
            var firstBlockedTagIndex = _indexOfBlockedTag.FirstOrDefault(index => index != -1);
            var tagBlocked = path.ToList()[firstBlockedTagIndex].TagNumber;
            var indexOfTagBlockedInList = TagListOfInFrontOfPartsReplacingWorkstation.IndexOf(tagBlocked);

            var workStationTag = TagListOfWorkstationInPartsReplacing[indexOfTagBlockedInList];
            var blockedWorkStation = StaMap.GetPointByTagNumber(workStationTag);

            if (VehicleToEntry.NavigationState.State != VehicleNavigationState.NAV_STATE.WAIT_TAG_PASSABLE_BY_EQ_PARTS_REPLACING)
            {
                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.WAIT_TAG_PASSABLE_BY_EQ_PARTS_REPLACING;

                for (int i = firstBlockedTagIndex; i >= 0; i--)
                {
                    var newPath = path.Take(i);
                    if (!newPath.Last().IsVirtualPoint)
                        return newPath;
                }
                return null;
                //return path.Take(firstBlockedTagIndex);
            }
            else
            {
                bool IsBlockedTagClosing()
                {
                    return TagListOfInFrontOfPartsReplacingWorkstation.Contains(path.ToList()[firstBlockedTagIndex].TagNumber);

                }
                while (IsBlockedTagClosing())
                {
                    if (VehicleToEntry.CurrentRunningTask().IsTaskCanceled)
                        return null;

                    (VehicleToEntry.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"等待設備維修-[{blockedWorkStation.Graph.Display}]..");
                    await Task.Delay(1000);
                }
                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.RUNNING;
                return await WorkStationPartsReplacingControl(VehicleToEntry, path);
                //return path;
            }
        }
        private static async Task<IEnumerable<MapPoint>> RegionControl(IAGV VehicleToEntry, IEnumerable<MapPoint> path, bool isVehicleAtEntryPointNow, MapRegion regionToGo = null, MapPoint finalGoal = null)
        {
            IEnumerable<MapPoint> finalPathUseWhenRegionCleared = null;
            if (isVehicleAtEntryPointNow)
            {
                finalPathUseWhenRegionCleared = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(VehicleToEntry.currentMapPoint, finalGoal, new List<MapPoint>());
            }

            IEnumerable<MapRegion> regionsPass = path.GetRegions();
            var nextRegion = isVehicleAtEntryPointNow ? regionToGo : regionsPass.Skip(1).FirstOrDefault(reg => reg.RegionType != MapRegion.MAP_REGION_TYPE.UNKNOWN);
            if (!isVehicleAtEntryPointNow && (nextRegion == null || nextRegion.RegionType == MapRegion.MAP_REGION_TYPE.UNKNOWN))
                return path;
            if (!isVehicleAtEntryPointNow && nextRegion.Name == VehicleToEntry.currentMapPoint.GetRegion(StaMap.Map).Name)
                return path;
            int capcityOfRegion = nextRegion.MaxVehicleCapacity;
            bool _isVehicleCapcityOfRegionFull()
            {
                IEnumerable<IAGV> inThisRegionVehicle = VMSManager.AllAGV.FilterOutAGVFromCollection(VehicleToEntry)
                                                                    .Where(vehicle => vehicle.states.Coordination.GetRegion(StaMap.Map).Name == nextRegion.Name);

                return inThisRegionVehicle.Count() >= capcityOfRegion;
            }
            if (_isVehicleCapcityOfRegionFull())
            {
                var entryTags = RegionManager.GetRegionEntryPoints(nextRegion);

                MapPoint entryPoint = entryTags.First();
                if (VehicleToEntry.NavigationState.State == VehicleNavigationState.NAV_STATE.WAIT_REGION_ENTERABLE)
                {
                    REGION_CONTROL_STATE currentRegionControlState = VehicleToEntry.NavigationState.RegionControlState;
                    if (currentRegionControlState == VehicleNavigationState.REGION_CONTROL_STATE.WAIT_AGV_CYCLE_STOP)
                    {
                        while (VehicleToEntry.main_state != clsEnums.MAIN_STATUS.IDLE)
                        {
                            (VehicleToEntry.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"等待抵達 [{nextRegion.Name}] 進入點");

                            if (VehicleToEntry.CurrentRunningTask().IsTaskCanceled)
                                throw new TaskCanceledException();
                            await Task.Delay(1000);
                        }
                        VehicleToEntry.NavigationState.RegionControlState = REGION_CONTROL_STATE.WAIT_AGV_REACH_ENTRY_POINT;
                        return MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(VehicleToEntry.currentMapPoint, entryPoint, new List<MapPoint>());
                    }

                    VehicleToEntry.NavigationState.ResetNavigationPoints();
                    while (_isVehicleCapcityOfRegionFull())
                    {
                        await StaMap.UnRegistPointsOfAGVRegisted(VehicleToEntry);
                        VehicleToEntry.NavigationState.ResetNavigationPoints();
                        if (VehicleToEntry.CurrentRunningTask().IsTaskCanceled)
                            return null;

                        (VehicleToEntry.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"等待區域-[{nextRegion.Name}]可進入");
                        await Task.Delay(1000);
                    }
                    VehicleToEntry.NavigationState.RegionControlState = REGION_CONTROL_STATE.NONE;
                    VehicleToEntry.NavigationState.State = NAV_STATE.RUNNING;
                    await VehicleToEntry.CurrentRunningTask().CycleStopRequestAsync();
                    return finalPathUseWhenRegionCleared;
                }
                if (!entryTags.Any())
                    throw new NoPathForNavigatorException();


                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.WAIT_REGION_ENTERABLE;
                bool _isRemainPathContainEntryPoint = VehicleToEntry.NavigationState.NextNavigtionPoints.Any(pt => pt.TagNumber == entryPoint.TagNumber);
                if (!_isRemainPathContainEntryPoint)
                {

                    VehicleToEntry.NavigationState.RegionControlState = VehicleNavigationState.REGION_CONTROL_STATE.WAIT_AGV_CYCLE_STOP;
                    await VehicleToEntry.CurrentRunningTask().CycleStopRequestAsync();
                    await Task.Delay(1000);
                    return null;
                }
                else
                {

                    var waitPtIndex = path.ToList().FindIndex(pt => pt.TagNumber == entryPoint.TagNumber);
                    VehicleToEntry.NavigationState.RegionControlState = VehicleNavigationState.REGION_CONTROL_STATE.WAIT_AGV_REACH_ENTRY_POINT;
                    return path.Take(waitPtIndex + 1);
                }


            }
            else if (isVehicleAtEntryPointNow)
            {
                return finalPathUseWhenRegionCleared;
            }
            else
            {
                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.RUNNING;
                return path;
            }

            //while (_isVehicleCapcityOfRegionFull())
            //{
            //    (VehicleToEntry.CurrentRunningTask() as MoveTaskDynamicPathPlanV2)
            //        .UpdateMoveStateMessage($"Wait {nextRegion.Name} 可進入..({capcityOfRegion})");
            //    await Task.Delay(1000);
            //}

        }

        public static void CancelDispatchRequest(IAGV vehicle)
        {
            DispatingVehicles.Remove(vehicle);
        }

        public static async Task<List<int>> TryGetBlockedTagByEQMaintainFromAGVS()
        {
            try
            {
                var response = await AGVSystemCommonNet6.Microservices.AGVS.AGVSSerivces.TRAFFICS.GetBlockedTagsByEqMaintain();
                if (response.confirm)
                {
                    return response.blockedTags;
                }
                else
                {
                    return new List<int>();
                }
            }
            catch (Exception ex)
            {
                LOG.ERROR(ex);
                return new List<int>();
            }
        }


        internal static void AddWorkStationInPartsReplacing(int workstationTag)
        {
            if (TagListOfWorkstationInPartsReplacing.Contains(workstationTag))
            {
                OnWorkStationStartPartsReplace?.Invoke("", workstationTag);
                return;
            }
            TagListOfWorkstationInPartsReplacing.Add(workstationTag);

            var cycleStopVehicles = VMSManager.AllAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                             .Where(agv => agv.NavigationState.NextNavigtionPoints.Any(pt => TagListOfInFrontOfPartsReplacingWorkstation.Contains(pt.TagNumber)));

            if (cycleStopVehicles.Any())
            {
                var frontPoints = StaMap.GetPointByTagNumber(workstationTag).TargetNormalPoints();
                var blockedTag = frontPoints.First().TagNumber;
                foreach (var _vehicle in cycleStopVehicles)
                {
                    Task.Run(() =>
                    {
                        bool cycleStopNeed = _vehicle.NavigationState.NextNavigtionPoints.ToList().FindIndex(pt => pt.TagNumber == blockedTag) > 1;
                        if (cycleStopNeed)
                        {
                            _vehicle.CurrentRunningTask().CycleStopRequestAsync();
                        }
                    });
                }
            }

            OnWorkStationStartPartsReplace?.Invoke("", workstationTag);
        }

        internal static void RemoveWorkStationInPartsReplacing(int workstationTag)
        {
            if (!TagListOfWorkstationInPartsReplacing.Contains(workstationTag))
            {
                OnWorkStationFinishPartsReplace?.Invoke("", workstationTag);
                return;
            }
            TagListOfWorkstationInPartsReplacing.Remove(workstationTag);
            OnWorkStationFinishPartsReplace?.Invoke("", workstationTag);
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

    public class clsVehicleNavigationController
    {

        public Dictionary<IAGV, clsVehicleNavigationController> OtherVehicleNavStateControllers

        {
            get
            {
                return DispatchCenter.CurrentNavingPathes.Where(v => v.Key.Name != vehicle.Name)
                                                         .ToDictionary(v => v.Key, v => v.Value);
            }

        }

        public readonly IAGV vehicle;
        public List<MapPoint> currentNavigationPlan { get; private set; } = new List<MapPoint>();

        public MapPoint NextStopablePoint { get; private set; } = new MapPoint();

        public Dictionary<MapRectangle, clsConflicState> currentConflics { get; private set; } = new Dictionary<MapRectangle, clsConflicState>();

        public clsVehicleNavigationController(IAGV vehicle)
        {
            this.vehicle = vehicle;
            //ConflicMonitorWorker();

        }


        public void Reset()
        {
            currentConflics.Clear();
            currentNavigationPlan.Clear();
        }

        public void UpdateNavagionPlan(List<MapPoint> _currentNavigationPlan)
        {
            Reset();
            currentNavigationPlan = _currentNavigationPlan;
            var conflics = GetConflics();
            var conflicsRegions = conflics.Where(kp => kp.Value.Any()).SelectMany(kp => kp.Value).ToList();

            if (conflicsRegions.Any())
            {
                foreach (var region in conflicsRegions)
                {
                    if (currentConflics.Keys.All(reg => reg.StartPointTag.TagNumber != region.StartPointTag.TagNumber && reg.EndPointTag.TagNumber != region.EndPointTag.TagNumber))
                    {
                        currentConflics.Add(region, new clsConflicState());
                        ConflicRegionManager.AddWaitingRegion(vehicle, region);
                    }
                }
            }
        }

        private async Task ConflicMonitorWorker()
        {
            await Task.Delay(1);

            while (true)
            {
                await Task.Delay(1);
                var conflics = GetConflics();


            }
        }

        public Dictionary<IAGV, List<MapRectangle>> GetConflics()
        {
            var othersStates = OtherVehicleNavStateControllers;

            var comflics = othersStates.ToDictionary(kp => kp.Key, kp => _GetConflicPathSegments(kp.Key, kp.Key.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING ?
                                                                                                                        new List<MapPoint>() : kp.Value.currentNavigationPlan));

            return comflics;
            List<MapRectangle> _GetConflicPathSegments(IAGV _pathOwner, List<MapPoint> _pathComparing)
            {
                List<MapPoint> _path = _pathComparing.ToList();
                if (!_path.Any())
                {
                    _path.Add(_pathOwner.currentMapPoint);
                }
                var _indexOfVehicleLoc = _path.FindIndex(pt => pt.TagNumber == _pathOwner.currentMapPoint.TagNumber);
                _path = _path.Skip(_indexOfVehicleLoc).ToList();

                return currentNavigationPlan.GetPathRegion(vehicle)
                                    .ToList()
                                     .Where(rect => _path.GetPathRegion(_pathOwner).ToList()
                                                                  .Any(_rect => _rect.IsIntersectionTo(rect)))
                                     .ToList();
            }
        }

        internal List<MapPoint> GetNextPath()
        {
            if (currentConflics.Any())
            {
                var notTogGoTag = currentConflics.First().Key.StartPointTag.TagNumber;
                var notTogGoEndTag = currentConflics.First().Key.EndPointTag.TagNumber;
                int ntTogoIndex = currentNavigationPlan.FindIndex(pt => pt.TagNumber == notTogGoTag);


                List<IAGV> waitingForVehicles = OtherVehicleNavStateControllers.Keys.Where(vehicle => vehicle.currentMapPoint.TagNumber == notTogGoTag
                                                                      || vehicle.currentMapPoint.TagNumber == notTogGoEndTag).ToList();

                List<MapPoint> _constrainPoints = new List<MapPoint>();
                _constrainPoints.AddRange(OtherVehicleNavStateControllers.Keys.Select(v => v.currentMapPoint));
                _constrainPoints.Add(StaMap.GetPointByTagNumber(notTogGoTag));
                _constrainPoints.Add(StaMap.GetPointByTagNumber(notTogGoEndTag));
                PathFinder _finder = new PathFinder();
                var pathesConsiderConstrains = _finder.FindPathes(StaMap.Map, vehicle.currentMapPoint, currentNavigationPlan.Last(), new PathFinderOption
                {
                    Strategy = PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE,
                    ConstrainTags = _constrainPoints.GetTagCollection().ToList()
                });
                pathesConsiderConstrains = pathesConsiderConstrains.OrderBy(info => info.total_rotation_angle).ToList();
                if (pathesConsiderConstrains.Any())
                {
                    Reset();
                    currentNavigationPlan = pathesConsiderConstrains.First().stations;
                    return GetNextPath();
                }
                var pathToGo = currentNavigationPlan.Take(ntTogoIndex).ToList();
                int notVirtualPtIndex = pathToGo.FindLastIndex(pt => !pt.IsVirtualPoint);

                currentNavigationPlan = pathToGo.Take(notVirtualPtIndex + 1).ToList();
                return currentNavigationPlan;

            }
            else
            {
                return currentNavigationPlan;
            }

        }
    }

    public class clsConflicState
    {
        public DateTime ReachConflicRegionPredictTime;
    }

    public class PathPlanner
    {
        public readonly IAGV Vehicle;
        public readonly clsTaskDto Order;
        public readonly VehicleMovementStage Stage;
        public readonly MapPoint FinalGoalPoint;

        public double LinearSpeed { get; set; } = 1;  // 直线速度，单位：米/秒
        public double RotationSpeed { get; set; } = 20;  // 旋转速度，单位：度/秒
        public double CurrentAngle { get; set; } = 0; // 当前角度，单位：度


        public int FinalGoalTag => FinalGoalPoint.TagNumber;
        public int VehicleCurrentTag => Vehicle.currentMapPoint.TagNumber;
        public MapPoint VehicleCurrentTPoint => Vehicle.currentMapPoint;

        public clsCoordination VehicleCurrentCoordination => Vehicle.states.Coordination;

        public PathPlanner(IAGV vehicle, clsTaskDto order, VehicleMovementStage stage)
        {
            Vehicle = vehicle;
            Order = order;
            Stage = stage;
            FinalGoalPoint = Order.GetFinalMapPoint(vehicle, stage);
        }

        public List<TimeWindow> StartWithCurrentTag()
        {
            PathFinder pf = new PathFinder();
            var pathFindeResult = pf.FindShortestPath(VehicleCurrentTag, FinalGoalTag, new PathFinder.PathFinderOption
            {
                OnlyNormalPoint = true,
            });
            CurrentAngle = VehicleCurrentCoordination.Theta;
            var timewindows = ProcessRoute(pathFindeResult.stations.Skip(1).Select(pt => Tuple.Create(pt.X, pt.Y)).ToList());
            return timewindows;
        }

        public List<TimeWindow> ProcessRoute(List<Tuple<double, double>> points)
        {
            double xCurrent = VehicleCurrentCoordination.X;
            double yCurrent = VehicleCurrentCoordination.Y;
            List<TimeWindow> _timewindows = new List<TimeWindow>();
            DateTime now = DateTime.Now;
            int _index = 0;
            foreach (var point in points)
            {
                double xNext = point.Item1;
                double yNext = point.Item2;

                // 计算目标角度
                double targetAngle = CalculateAngle(xCurrent, yCurrent, xNext, yNext);

                // 计算旋转时间
                double rotationTime = CalculateRotationTime(targetAngle);
                Console.WriteLine($"Rotate {rotationTime} seconds to face ({xNext}, {yNext})");

                // 计算行驶时间
                double distance = CalculateDistance(xCurrent, yCurrent, xNext, yNext);
                double travelTime = CalculateTravelTime(distance);
                Console.WriteLine($"Travel {travelTime} seconds to reach ({xNext}, {yNext})");
                now = now.AddSeconds(travelTime + rotationTime);
                _timewindows.Add(new TimeWindow(now.ToOADate(), now.ToOADate(), Tuple.Create(xNext, yNext)));
                if (_index > 0)
                {
                    _timewindows[_index - 1].EndTime = now.ToOADate();
                }
                // 更新位置和角度
                xCurrent = xNext;
                yCurrent = yNext;
                UpdateAngle(targetAngle);
                _index += 1;
            }
            return _timewindows;
        }

        // 计算两点间的距离
        private double CalculateDistance(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }

        // 计算角度
        private double CalculateAngle(double x1, double y1, double x2, double y2)
        {
            return Math.Atan2(y2 - y1, x2 - x1) * (180 / Math.PI);
        }

        // 计算旋转所需时间
        private double CalculateRotationTime(double targetAngle)
        {
            double angleDifference = Math.Abs(targetAngle - CurrentAngle);
            return angleDifference / RotationSpeed;
        }

        // 计算直线行驶时间
        private double CalculateTravelTime(double distance)
        {
            return distance / LinearSpeed;
        }

        // 更新车辆的当前角度
        private void UpdateAngle(double targetAngle)
        {
            CurrentAngle = targetAngle;
        }

    }
}

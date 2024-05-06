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
using System.Drawing;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static AGVSystemCommonNet6.Vehicle_Control.CarComponent;

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

        public static List<IAGV> DispatingVehicles = new List<IAGV>();

        public static void Initialize()
        {

        }

        public static async Task<IEnumerable<MapPoint>> MoveToGoalGetPath(IAGV requestVehicle, MapPoint startPoint, clsTaskDto taskDto, VehicleMovementStage stage)
        {
            MapPoint finalGoal = taskDto.GetFinalMapPoint(requestVehicle, stage);
            PathFinder finder = new PathFinder();
            List<clsPathInfo> pathes = finder.FindPathes(map, startPoint, finalGoal, new PathFinder.PathFinderOption
            {
                OnlyNormalPoint = true,
                Strategy = PathFinder.PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE
            });

            var orderedPathesInfo = pathes.OrderBy(path => path.total_rotation_angle)
                                          .OrderBy(path => path.GetRegistPointsNum()).ToList();

            orderedPathesInfo = orderedPathesInfo.Where(_pInfo => !_IsPathContainChargeStationOfVehicleChargingOutPoint(_pInfo))
                                                .ToList();
            var pathInfoFind = orderedPathesInfo.FirstOrDefault();

            if (pathInfoFind == null)
                return null;

            (int num, IEnumerable<MapPoint> registedPoints) = pathInfoFind.GetRegistPoints(requestVehicle);

            //if (num != 0)
            //{
            //    var fisrtRegistedPt = registedPoints.First();
            //    var index= pathInfoFind.stations.FindIndex(pt => pt.TagNumber == fisrtRegistedPt.TagNumber);
            //    var tempStopPt= pathInfoFind.stations.Take(index).First(pt => pt.CalculateDistance(fisrtRegistedPt) >= 2);

            //    var tempStopPtIndex = pathInfoFind.stations.FindIndex(pt => pt.TagNumber == tempStopPt.TagNumber);
            //    return pathInfoFind.stations.Take(tempStopPtIndex+1);
            //}

            return pathInfoFind.stations;

            foreach (var item in orderedPathesInfo)
            {

            }

            return new List<MapPoint>();


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
            bool _isInNarrowRegion = vehicle.currentMapPoint.GetRegion(StaMap.Map).IsNarrowPath;
            vehicle.NavigationState.UpdateNavigationPoints(optimizePath_Init);
            var usableSubGoals = optimizePath_Init.Skip(1).Where(pt => pt.CalculateDistance(vehicle.currentMapPoint) >= 3.5)
                                                          .Where(pt => !pt.IsVirtualPoint && !GetConstrains().GetTagCollection().Contains(pt.TagNumber))
                                                          //.Where(pt => otherAGV.All(agv => pt.CalculateDistance(agv.currentMapPoint) >= (_isInNarrowRegion ? 2 : 2.5)))
                                                          //.Where(pt => otherAGV.All(agv => !agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetCircleArea(ref vehicle, 0.2).IsIntersectionTo(vehicle.AGVRotaionGeometry))))
                                                          .ToList();

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
                    if (vehicle.NavigationState.State != VehicleNavigationState.NAV_STATE.WAIT_REGION_ENTERABLE)
                    {
                        var pathPlanViaWorkStationPartsReplace = await WorkStationPartsReplacingControl(vehicle, path);
                        if (pathPlanViaWorkStationPartsReplace != null &&
                            !pathPlanViaWorkStationPartsReplace.GetTagCollection().SequenceEqual(path.GetTagCollection()))
                        {
                            return pathPlanViaWorkStationPartsReplace;
                        }
                    }
                    return await RegionControl(vehicle, path);
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
                vehicle.NavigationState.UpdateNavigationPoints(new List<MapPoint>() { vehicle.currentMapPoint });
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
                        item.NavigationState.UpdateNavigationPoints(new List<MapPoint> { item.currentMapPoint });
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
                            vehicle.NavigationState.UpdateNavigationPoints(new List<MapPoint> { vehicle.currentMapPoint });
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
                                vehicle.NavigationState.UpdateNavigationPoints(new List<MapPoint>() { vehicle.currentMapPoint });

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
                                vehicle.NavigationState.UpdateNavigationPoints(new List<MapPoint>() { vehicle.currentMapPoint });

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
                            bool _geometryConflic = item.IsIntersectionTo(_otherAGV.AGVGeometery);
                            bool _pathConflic = _otherAGV.NavigationState.OccupyRegions.Any(reg => reg.IsIntersectionTo(item));
                            isConflic = _pathConflic || _geometryConflic;
                            if (isConflic)
                            {
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
                //constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetVehicleOverlapPoint(_vehicle)));
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
        private static async Task<IEnumerable<MapPoint>> RegionControl(IAGV VehicleToEntry, IEnumerable<MapPoint> path)
        {
            IEnumerable<MapRegion> regionsPass = path.GetRegions();
            var nextRegion = regionsPass.Skip(1).FirstOrDefault(reg => reg.RegionType != MapRegion.MAP_REGION_TYPE.UNKNOWN);
            if (nextRegion == null || nextRegion.RegionType == MapRegion.MAP_REGION_TYPE.UNKNOWN) return path;
            if (nextRegion.Name == VehicleToEntry.currentMapPoint.GetRegion(StaMap.Map).Name) return path;
            int capcityOfRegion = nextRegion.MaxVehicleCapacity;
            bool _isVehicleCapcityOfRegionFull()
            {
                IEnumerable<IAGV> inThisRegionVehicle = VMSManager.AllAGV.FilterOutAGVFromCollection(VehicleToEntry)
                                                                    .Where(vehicle => vehicle.states.Coordination.GetRegion(StaMap.Map).Name == nextRegion.Name);

                return inThisRegionVehicle.Count() >= capcityOfRegion;
            }
            if (_isVehicleCapcityOfRegionFull())
            {
                if (VehicleToEntry.NavigationState.State == VehicleNavigationState.NAV_STATE.WAIT_REGION_ENTERABLE)
                {
                    VehicleToEntry.NavigationState.ResetNavigationPoints();
                    while (_isVehicleCapcityOfRegionFull())
                    {
                        if (VehicleToEntry.CurrentRunningTask().IsTaskCanceled)
                            return null;

                        (VehicleToEntry.CurrentRunningTask() as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"等待區域-[{nextRegion.Name}]可進入");
                        await Task.Delay(100);
                    }
                    return path;
                }
                var entryTags = RegionManager.GetRegionEntryPoints(nextRegion);
                var waitPoint = entryTags.OrderBy(pt => pt.CalculateDistance(VehicleToEntry.currentMapPoint)).FirstOrDefault();
                if (waitPoint == null) return path;

                var waitPtIndex = path.ToList().FindIndex(pt => pt.TagNumber == waitPoint.TagNumber);
                if (waitPtIndex < 0)
                {
                    return path;
                }
                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.WAIT_REGION_ENTERABLE;
                return path.Take(waitPtIndex + 1);
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

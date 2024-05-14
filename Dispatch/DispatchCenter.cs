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
        public static DeadLockMonitor TrafficDeadLockMonitor = new DeadLockMonitor();
        public static List<IAGV> DispatingVehicles = new List<IAGV>();

        public static void Initialize()
        {
            TrafficDeadLockMonitor.StartAsync();
        }

        public static async Task<IEnumerable<MapPoint>> MoveToDestineDispatchRequest(IAGV vehicle, MapPoint startPoint, clsTaskDto taskDto, VehicleMovementStage stage)
        {

            if (!DispatingVehicles.Contains(vehicle))
                DispatingVehicles.Add(vehicle);
            MapPoint finalMapPoint = taskDto.GetFinalMapPoint(vehicle, stage);
            var path = await GenNextNavigationPath(vehicle, startPoint, taskDto, stage);
            return path = path == null ? path : path.Clone();

        }
        private static async Task<IEnumerable<MapPoint>> GenNextNavigationPath(IAGV vehicle, MapPoint startPoint, clsTaskDto order, VehicleMovementStage stage)
        {


            vehicle.NavigationState.ResetNavigationPointsOfPathCalculation();
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            MapPoint finalMapPoint = order.GetFinalMapPoint(vehicle, stage);
            IEnumerable<MapPoint> optimizePath_Init_No_constrain = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, new List<MapPoint>());
            IEnumerable<MapPoint> optimizePath_Init = null;

            try
            {
                optimizePath_Init = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, GetConstrains(vehicle, otherAGV, finalMapPoint));
            }
            catch (Exception)
            {
                optimizePath_Init = optimizePath_Init_No_constrain;
            }

            Dictionary<MapPoint, bool> wayStates = optimizePath_Init.ToDictionary(pt => pt, pt => pt.IsSingleWay());
            IEnumerable<MapPoint> optimizePathFound = null;
            MapRegion vehicleCurrentRegion = vehicle.currentMapPoint.GetRegion(CurrentMap);
            MapRegion finalPointRegion = finalMapPoint.GetRegion(CurrentMap);

            var outPtOfSingleWay = optimizePath_Init.GetOutPointOfPathWithSingleWay();

            bool _isInNarrowRegion = vehicleCurrentRegion.IsNarrowPath;
            vehicle.NavigationState.UpdateNavigationPointsForPathCalculation(optimizePath_Init);
            var usableSubGoals = optimizePath_Init.Skip(0).Where(pt => pt.CalculateDistance(vehicle.currentMapPoint) >= (_isInNarrowRegion ? 2.5 : 2.5))
                                                          .Where(pt => !pt.IsVirtualPoint && !GetConstrains(vehicle, otherAGV, finalMapPoint).GetTagCollection().Contains(pt.TagNumber))
                                                          .Where(pt => otherAGV.All(agv => pt.CalculateDistance(agv.currentMapPoint) >= (_isInNarrowRegion ? 2 : 1.5)))
                                                          //.Where(pt => otherAGV.All(agv => !agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetCircleArea(ref vehicle, 0.2).IsIntersectionTo(vehicle.AGVRotaionGeometry))))
                                                          .ToList();
            if (outPtOfSingleWay != null)
            {
                usableSubGoals.Add(outPtOfSingleWay);
                usableSubGoals = usableSubGoals.DistinctBy(pt => pt.TagNumber).OrderBy(pt => pt.CalculateDistance(vehicle.currentMapPoint)).ToList();
            }
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
                    var path = new List<MapPoint>();
                    if (_isAnyVehicleInWorkStationOfNarrowRegion(finalPointRegion))
                        path = subGoalResults.Last(path => path != null).ToList();
                    else
                        path = subGoalResults.First(path => path != null).ToList();
                    if (path != null)
                    {
                        bool willConflicMaybe = otherAGV.Any(_vehicle => _vehicle.currentMapPoint.StationType != MapPoint.STATION_TYPE.Charge && _vehicle.AGVRotaionGeometry.IsIntersectionTo(path.Last().GetCircleArea(ref vehicle, 1.1)));

                        return willConflicMaybe ? null : path;


                    }
                    return path;
                }
                catch (Exception ex)
                {
                    LOG.ERROR(ex);
                    return null;
                }
            }
            else
            {
                vehicle.NavigationState.ResetNavigationPointsOfPathCalculation();
                return null;
            }
            async Task<IEnumerable<MapPoint>> FindPath(IAGV vehicle, IEnumerable<IAGV> otherAGV, MapPoint _finalMapPoint, IEnumerable<MapPoint> oriOptimizePath, bool autoSolve = false)

            {
                IEnumerable<MapPoint> _optimizePath = null;
                List<IAGV> otherDispatingVehicle = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle).ToList();
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
                    MapPoint conflicRegionEndPt = StaMap.GetPointByTagNumber(conflicRegion.EndMapPoint.TagNumber);
                }
                else
                    _optimizePath = oriOptimizePath;
                return _optimizePath;

                static bool FindConflicRegion(IAGV vehicle, List<IAGV> otherDispatingVehicle, out MapRectangle _conflicRegion)
                {
                    _conflicRegion = null;
                    foreach (var item in vehicle.NavigationState.NextPathOccupyRegionsForPathCalculation)
                    {
                        bool isConflic = false;
                        foreach (var _otherAGV in otherDispatingVehicle)
                        {
                            bool _isAtChargeStation = _otherAGV.currentMapPoint.IsCharge;
                            bool _geometryConflic = item.IsIntersectionTo(_otherAGV.AGVRealTimeGeometery);
                            bool _pathConflic = _otherAGV.NavigationState.NextPathOccupyRegions.Any(reg => reg.IsIntersectionTo(item));
                            isConflic = _pathConflic || _geometryConflic;
                            if (isConflic)
                            {
                                (vehicle.CurrentRunningTask() as MoveTaskDynamicPathPlanV2)
                                    .UpdateMoveStateMessage($"{item.StartPointTag.TagNumber}-{item.EndMapPoint.TagNumber} Conflic To {_otherAGV.Name}.\r\n(_geometryConflic:{_geometryConflic}/_pathConflic:{_pathConflic})");
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
        }
        private static void GetAllPathToDestine(MapPoint startPt, MapPoint destinePt)
        {
            PathFinder _pf = new PathFinder();
            _pf.FindPathes(CurrentMap, startPt, destinePt, new PathFinderOption
            {
                Strategy = PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE,
                OnlyNormalPoint = true,
            });
        }
        public static List<MapPoint> GetConstrains(IAGV MainVehicle, IEnumerable<IAGV>? otherAGV, MapPoint finalMapPoint)
        {
            List<MapPoint> constrains = new List<MapPoint>();
            var registedPoints = StaMap.RegistDictionary.Where(pari => pari.Value.RegisterAGVName != MainVehicle.Name).Select(p => StaMap.GetPointByTagNumber(p.Key)).ToList();
            constrains.AddRange(registedPoints);

            IEnumerable<MapPoint> _GetVehicleEnteredEntryPoint(IAGV _vehicle)
            {
                if (!_vehicle.currentMapPoint.IsCharge)
                    return new List<MapPoint>();

                return _vehicle.currentMapPoint.Target.Keys
                                                .Select(index => StaMap.GetPointByIndex(index));
            }

            IEnumerable<MapPoint> _GetOtherVehicleChargeStationEnteredEntryPoint(IAGV _vehicle)
            {
                if (!_vehicle.currentMapPoint.IsCharge)
                    return new List<MapPoint>();

                return _vehicle.currentMapPoint.Target.Keys
                                                .Select(index => StaMap.GetPointByIndex(index));
            }

            IEnumerable<MapPoint> _GetVehicleOverlapPoint(IAGV _vehicle)
            {
                return StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal)
                                                .Where(pt => pt.GetCircleArea(ref MainVehicle, 0.5).IsIntersectionTo(_vehicle.AGVRotaionGeometry));
            }


            //constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetVehicleEnteredEntryPoint(_vehicle)));
            constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetOtherVehicleChargeStationEnteredEntryPoint(_vehicle)));
            constrains.AddRange(otherAGV.SelectMany(_vehicle => _vehicle.NavigationState.NextNavigtionPoints));
            //constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetVehicleOverlapPoint(_vehicle)));
            //var blockedTags = TryGetBlockedTagByEQMaintainFromAGVS().GetAwaiter().GetResult();
            constrains.AddRange(StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && !pt.Enable));
            //constrains.AddRange(blockedTags.Select(tag => StaMap.GetPointByTagNumber(tag)));
            constrains = constrains.DistinctBy(st => st.TagNumber).ToList();
            constrains = constrains.Where(pt => pt.TagNumber != finalMapPoint.TagNumber).ToList();
            return constrains;
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

}

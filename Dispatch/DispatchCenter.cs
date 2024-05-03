﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using Microsoft.AspNetCore.Localization;
using RosSharp.RosBridgeClient.MessageTypes.Geometry;
using System;
using System.Drawing;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.Vehicle_Control.CarComponent;

namespace VMSystem.Dispatch
{
    public static class DispatchCenter
    {
        public static Scheduler PathScheduler = new Scheduler();
        public static Dictionary<IAGV, PathPlanner> VehicleOrderStore { get; set; } = new Dictionary<IAGV, PathPlanner>();

        public static List<IAGV> DispatingVehicles = new List<IAGV>();

        public static async Task<IEnumerable<MapPoint>> MoveToDestineDispatchRequest(IAGV vehicle, clsTaskDto taskDto, VehicleMovementStage stage)
        {

            if (!DispatingVehicles.Contains(vehicle))
                DispatingVehicles.Add(vehicle);
            var path = await GenNextNavigationPath(vehicle, taskDto, stage);

            return path;
            //if (VehicleOrderStore.ContainsKey(vehicle))
            //    VehicleOrderStore[vehicle] = new PathPlanner(vehicle, taskDto, stage);
            //else
            //    VehicleOrderStore.Add(vehicle, new PathPlanner(vehicle, taskDto, stage));

            //var timewindows = VehicleOrderStore[vehicle].StartWithCurrentTag();
            //foreach (var item in timewindows)
            //{
            //    PathScheduler.AddTimeWindow(item);
            //}
            //PathScheduler.CheckForConflicts();
        }
        private static async Task<IEnumerable<MapPoint>> GenNextNavigationPath(IAGV vehicle, clsTaskDto order, VehicleMovementStage stage)
        {
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            MapPoint finalMapPoint = order.GetFinalMapPoint(vehicle, stage);

            var registedPoints = StaMap.RegistDictionary.Where(pari => pari.Value.RegisterAGVName != vehicle.Name).Select(p => StaMap.GetPointByTagNumber(p.Key));

            IEnumerable<MapPoint> optimizePath_Init_No_constrain = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, finalMapPoint, new List<MapPoint>());
            IEnumerable<MapPoint> optimizePath_Init = null;

            try
            {
                optimizePath_Init = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, finalMapPoint, registedPoints);
            }
            catch (Exception)
            {
                optimizePath_Init = optimizePath_Init_No_constrain;
            }


            IEnumerable<MapPoint> optimizePathFound = null;
            vehicle.NavigationState.UpdateNavigationPoints(optimizePath_Init);
            var usableSubGoals = optimizePath_Init.Skip(1).Where(pt => pt.CalculateDistance(vehicle.currentMapPoint) > 3)
                                                            .Where(pt => !pt.IsVirtualPoint && !registedPoints.GetTagCollection().Contains(pt.TagNumber))
                                                            .Where(pt => otherAGV.All(agv => !agv.AGVRotaionGeometry.IsIntersectionTo(pt.GetCircleArea(ref vehicle, 1.2))));

            usableSubGoals = usableSubGoals.Any() ? usableSubGoals : new List<MapPoint>() { finalMapPoint };
            List<IEnumerable<MapPoint>> subGoalResults = new List<IEnumerable<MapPoint>>();

            foreach (var point in usableSubGoals)
            {
                var optmizePath_init_sub = optimizePath_Init.Where(pt => optimizePath_Init.ToList().IndexOf(pt) <= optimizePath_Init.ToList().IndexOf(point));
                vehicle.NavigationState.UpdateNavigationPoints(optmizePath_init_sub);
                var result = await FindPath(vehicle, otherAGV, point, optmizePath_init_sub, false);
                subGoalResults.Add(result);
            }

            if (!subGoalResults.All(path => path == null))
            {
                return subGoalResults.First(path => path != null && !path.IsPathHasPointsBeRegisted(vehicle, out var _registedPoints));
            }
            else
            {
                vehicle.NavigationState.UpdateNavigationPoints(new List<MapPoint>() { vehicle.currentMapPoint });
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

            static async Task<IEnumerable<MapPoint>> FindPath(IAGV vehicle, IEnumerable<IAGV> otherAGV, MapPoint _finalMapPoint, IEnumerable<MapPoint> oriOptimizePath, bool autoSolve = false)
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
                                var _optimizePathTryGet = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, _finalMapPoint, otherAGV.Select(agv => agv.currentMapPoint));
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


                                var _optimizePathTryGet = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, _finalMapPoint, otherAGV.Select(agv => agv.currentMapPoint));
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
                            isConflic = _otherAGV.NavigationState.OccupyRegions.Any(reg => reg.IsIntersectionTo(item)) || item.IsIntersectionTo(_otherAGV.AGVGeometery);
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
        }


        public static void CancelDispatchRequest(IAGV vehicle)
        {
            DispatingVehicles.Remove(vehicle);
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

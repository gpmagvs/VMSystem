using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using Microsoft.AspNetCore.Localization;
using RosSharp.RosBridgeClient.MessageTypes.Geometry;
using VMSystem.AGV;
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

            return await GenNextNavigationPath(vehicle, taskDto, stage);

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
            IEnumerable<MapPoint> optimizePath = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, finalMapPoint, new List<MapPoint>());
            vehicle.NavigationState.UpdateNavigationPoints(optimizePath);

            optimizePath = await FindPath(vehicle, otherAGV, finalMapPoint, optimizePath);

            return optimizePath;

            static async Task<IEnumerable<MapPoint>> FindPath(IAGV vehicle, IEnumerable<IAGV> otherAGV, MapPoint finalMapPoint, IEnumerable<MapPoint> optimizePath)
            {
                List<IAGV> otherDispatingVehicle = DispatingVehicles.FilterOutAGVFromCollection(vehicle).ToList();

                if (otherDispatingVehicle.Count == 0)
                {
                    otherDispatingVehicle.AddRange(otherAGV);

                    foreach (var item in otherDispatingVehicle)
                    {
                        item.NavigationState.UpdateNavigationPoints(new List<MapPoint> { item.currentMapPoint });
                    }
                }

                bool IsPathConflic(IEnumerable<MapRectangle> path1, IEnumerable<MapRectangle> path2, out IEnumerable<MapRectangle> conflicRegions)
                {
                    conflicRegions = path1.Where(rect1 => path2.Where(rect2 => rect2.IsIntersectionTo(rect1)).Any());
                    return conflicRegions.Any();
                }
                MapRectangle conflicRegion = null;
                foreach (var item in vehicle.NavigationState.OccupyRegions)
                {
                    bool isConflic = false;
                    foreach (var _otherAGV in otherDispatingVehicle)
                    {
                        isConflic = _otherAGV.NavigationState.OccupyRegions.Any(reg => reg.IsIntersectionTo(item));
                        if (isConflic)
                        {
                            break;
                        }
                    }
                    if (isConflic)
                    {
                        conflicRegion = item;
                        break;
                    }
                }
                if (conflicRegion != null)
                {
                    bool willConflicRegionReleaseFuture = false;
                    MapPoint conflicRegionStartPt = StaMap.GetPointByTagNumber(conflicRegion.StartPointTag.TagNumber);
                    MapPoint conflicRegionEndPt = StaMap.GetPointByTagNumber(conflicRegion.EndPointTag.TagNumber);
                    //var conflicRegionOwners = otherDispatingVehicle.Where(agv => agv.currentMapPoint == conflicRegionStartPt || agv.currentMapPoint == conflicRegionEndPt);


                    var conflicRegionOwners = otherDispatingVehicle.Where(agv => agv.NavigationState.OccupyRegions.Any(reg => reg.StartPointTag.TagNumber == conflicRegion.StartPointTag.TagNumber
                                                                            || reg.EndPointTag.TagNumber == conflicRegion.EndPointTag.TagNumber));

                    while (otherDispatingVehicle.Where(agv => agv.NavigationState.OccupyRegions.Any(reg => reg.StartPointTag.TagNumber == conflicRegion.StartPointTag.TagNumber
                                                                            || reg.EndPointTag.TagNumber == conflicRegion.EndPointTag.TagNumber)).Any())
                    {
                        await Task.Delay(1000);
                        Dictionary<IAGV, bool> IsAGVExecutingTASK = new Dictionary<IAGV, bool>();
                        foreach (var _agv in conflicRegionOwners)
                        {
                            var orderState = _agv.taskDispatchModule.OrderExecuteState;
                            if (orderState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                //任務進行中
                                var currentTask = _agv.CurrentRunningTask();
                                vehicle.CurrentRunningTask().TrafficWaitingState.SetDisplayMessage($"Wait Conflic Release");
                                IsAGVExecutingTASK.Add(_agv, true);
                            }
                            else
                            {
                                IsAGVExecutingTASK.Add(_agv, false);
                                vehicle.CurrentRunningTask().TrafficWaitingState.SetDisplayMessage($"Ready To Move {_agv.Name} fuck away.");
                                //IDLE
                            }
                        }

                        if (IsAGVExecutingTASK.Values.All(s => !s))
                        {
                            var constrains = IsAGVExecutingTASK.Keys.Select(v => v.currentMapPoint);
                            IEnumerable<MapPoint> _newPathTryFound = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(vehicle.currentMapPoint, finalMapPoint, constrains);
                            if (_newPathTryFound.Any())
                            {
                                vehicle.NavigationState.UpdateNavigationPoints(_newPathTryFound);
                                await FindPath(vehicle, otherAGV, finalMapPoint, _newPathTryFound);
                            }
                        }

                        vehicle.NavigationState.UpdateNavigationPoints(new List<MapPoint> { vehicle.currentMapPoint });
                        await Task.Delay(1000);
                    }
                    if (conflicRegionOwners.Any())
                    {

                    }
                }

                return optimizePath;
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

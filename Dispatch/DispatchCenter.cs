using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.TrafficControl.VehicleNavigationState;

namespace VMSystem.Dispatch
{
    public static class DispatchCenter
    {
        internal static List<int> TagListOfWorkstationInPartsReplacing { get; private set; } = new List<int>();
        internal static event EventHandler<int> OnWorkStationStartPartsReplace;
        internal static event EventHandler<int> OnWorkStationFinishPartsReplace;
        private static SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
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

        public static void Initialize()
        {
            TrafficDeadLockMonitor.StartAsync();
            VehicleNavigationState.OnAGVStartWaitConflicSolve += TrafficDeadLockMonitor.HandleVehicleStartWaitConflicSolve;
            VehicleNavigationState.OnAGVNoWaitConflicSolve += TrafficDeadLockMonitor.HandleVehicleNoWaitConflicSolve;
        }

        public static async Task<IEnumerable<MapPoint>> MoveToDestineDispatchRequest(IAGV vehicle, MapPoint startPoint, clsTaskDto taskDto, VehicleMovementStage stage)
        {
            try
            {
                await semaphore.WaitAsync();
                MapPoint finalMapPoint = taskDto.GetFinalMapPoint(vehicle, stage);
                var path = await GenNextNavigationPath(vehicle, startPoint, taskDto, stage);
                return path = path == null ? path : path.Clone();
            }
            catch (Exception ex)
            {
                return null;
            }
            finally
            {
                await Task.Delay(10);
                semaphore.Release();
            }

        }
        private static async Task<IEnumerable<MapPoint>> GenNextNavigationPath(IAGV vehicle, MapPoint startPoint, clsTaskDto order, VehicleMovementStage stage)
        {
            vehicle.NavigationState.ResetNavigationPointsOfPathCalculation();
            var otherAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
            MapPoint finalMapPoint = order.GetFinalMapPoint(vehicle, stage);
            IEnumerable<MapPoint> optimizePath_Init_No_constrain = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, new List<MapPoint>(), vehicle.states.Coordination.Theta);
            IEnumerable<MapPoint> optimizePath_Init = null;

            try
            {
                optimizePath_Init = MoveTaskDynamicPathPlanV2.LowLevelSearch.GetOptimizedMapPoints(startPoint, finalMapPoint, GetConstrains(vehicle, otherAGV, finalMapPoint), vehicle.states.Coordination.Theta);
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
                                                          .Where(pt => !pt.IsVirtualPoint && !GetConstrains(vehicle, otherAGV, finalMapPoint).GetTagCollection().Contains(pt.TagNumber))
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

                    IEnumerable<MapPoint> _noConflicPathToDestine = subGoalResults.FirstOrDefault(_path => _path != null &&  _path.Last().TagNumber == finalMapPoint.TagNumber && _path.All(pt=>otherAGV.SelectMany(agv=>agv.NavigationState.NextNavigtionPoints).GetTagCollection().Contains(pt.TagNumber)));

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

                        return isTooCloseWithVehicleEntryPointOFWorkStation || otherAGV.Any(_vehicle => _vehicle.currentMapPoint.StationType != MapPoint.STATION_TYPE.Charge && _vehicle.AGVRotaionGeometry.IsIntersectionTo(_path.Last().GetCircleArea(ref vehicle, 1.1)));
                    }

                    #endregion
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
                                if (_isPointConflic)
                                    vehicleRunningTask?.UpdateMoveStateMessage($"Point {item.StartPoint.TagNumber} Conflic To {_otherAGV.Name}");
                                else
                                    vehicleRunningTask?.UpdateMoveStateMessage($"Path From {item.StartPoint.TagNumber} To {item.EndPoint.TagNumber} Conflic To {_otherAGV.Name}");
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
                                                .Where(pt => pt.CalculateDistance(_vehicle.states.Coordination) <= _vehicle.AGVRotaionGeometry.RotationRadius);
            }

            var disabledPoints = StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && !pt.Enable);

            if (TrafficControlCenter.TrafficControlParameters.DisableChargeStationEntryPointWhenNavigation)
            {
                constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetOtherVehicleChargeStationEnteredEntryPoint(_vehicle)));//當有車子在充電，充電站進入點不可用
            }

            constrains.AddRange(otherAGV.SelectMany(_vehicle => _vehicle.NavigationState.NextNavigtionPoints));//其他車輛當前導航路徑不可用
            //constrains.AddRange(otherAGV.SelectMany(_vehicle => _GetVehicleEnteredEntryPoint(_vehicle)));
            constrains.AddRange(otherAGV.Where(v => v.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None)
                                        .Where(v => v.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal)
                                        .SelectMany(_vehicle => _GetVehicleOverlapPoint(_vehicle))); //其他車輛當前位置有被旋轉區域範圍內涵蓋到的點不可用
            constrains.AddRange(StaMap.Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal && !pt.Enable));//圖資中Enable =False的點位不可用
            constrains = constrains.DistinctBy(st => st.TagNumber).ToList();
            List<MapPoint> additionRegists = constrains.SelectMany(pt => pt.RegistsPointIndexs.Select(_index => StaMap.GetPointByIndex(_index))).ToList();
            constrains.AddRange(additionRegists);
            if (MainVehicle.NavigationState.CurrentConflicRegion != null)
            {
                if (MainVehicle.NavigationState.CurrentConflicRegion.EndPoint.TagNumber != MainVehicle.NavigationState.CurrentConflicRegion.StartPoint.TagNumber)
                    constrains.Add(MainVehicle.NavigationState.CurrentConflicRegion.EndPoint);
            }
            if (additionRegists.Any())
            {
                //NotifyServiceHelper.WARNING($"{string.Join(",", additionRegists.GetTagCollection())} As Constrain By Pt Setting");
            }
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

                    VehicleToEntry.CurrentRunningTask().UpdateMoveStateMessage($"等待設備維修-[{blockedWorkStation.Graph.Display}]..");
                    await Task.Delay(1000);
                }
                VehicleToEntry.NavigationState.State = VehicleNavigationState.NAV_STATE.RUNNING;
                return await WorkStationPartsReplacingControl(VehicleToEntry, path);
                //return path;
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

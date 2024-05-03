using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
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
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        //public override async Task SendTaskToAGV()
        //{
        //    IsTaskCanceled = false;
        //    PassedTags.Clear();

        //    MapPoint finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);

        //    LowLevelSearch.GetOptimizedMapPoints(this.Agv.currentMapPoint, finalMapPoint);
        //    MapPoint _tempGoal = finalMapPoint;

        //    MapRegion finalGoalRegion = finalMapPoint.GetRegion(CurrentMap);
        //    finalGoalRegion = finalGoalRegion == null ? new MapRegion { IsNarrowPath = false, Name = "" } : finalGoalRegion;
        //    DestineTag = finalMapPoint.TagNumber;


        //    var optimizePathGolbal = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, finalMapPoint);

        //    var regions = optimizePathGolbal.GetRegions(CurrentMap)
        //                                    .Where(regin => regin != null && regin.Name != null)
        //                                    .Distinct();

        //    Dictionary<MapPoint, bool> checkPointIsRegionEntryOrLeavingPoint = new Dictionary<MapPoint, bool>();

        //    if (regions.Any())
        //    {
        //        //切成數個小終點
        //        var AgvCurrentRegion = Agv.currentMapPoint.GetRegion(CurrentMap);
        //        bool _IsAGVChangeRegion = AgvCurrentRegion != finalGoalRegion;
        //        if (AgvCurrentRegion != null && AgvCurrentRegion.LeavingTags.Any())
        //        {
        //            int currentRegionAGVNum = AgvCurrentRegion.GetCurrentVehicleNum(CurrentMap, VMSManager.AllAGV.Select(v => v.currentMapPoint));
        //            bool _isAllAgvInCurrentRegion = currentRegionAGVNum == VMSManager.AllAGV.Count;
        //            MapPoint leavingPoint = AgvCurrentRegion.GetNearLeavingPoint(CurrentMap, Agv.currentMapPoint);
        //            if (!_isAllAgvInCurrentRegion && _IsAGVChangeRegion && leavingPoint != null)
        //                checkPointIsRegionEntryOrLeavingPoint.Add(leavingPoint, true);//離開該區域
        //        }
        //        foreach (var region in regions.Where(reg => reg != AgvCurrentRegion))
        //        {
        //            MapPoint entryPoint = region.GetNearEntryPoint(CurrentMap, Agv.currentMapPoint);
        //            int NextRegionAGVNum = region.GetCurrentVehicleNum(CurrentMap, VMSManager.AllAGV.Select(v => v.currentMapPoint));
        //            bool _isNextRegionHasAGV = NextRegionAGVNum != 0;

        //            if (_isNextRegionHasAGV && _IsAGVChangeRegion && entryPoint != null)
        //                checkPointIsRegionEntryOrLeavingPoint.Add(entryPoint, true);
        //        }
        //    }


        //    if (!checkPointIsRegionEntryOrLeavingPoint.Any())
        //        checkPointIsRegionEntryOrLeavingPoint = new Dictionary<MapPoint, bool>()
        //        {
        //            {finalMapPoint,false }
        //        };
        //    else
        //    {
        //        checkPointIsRegionEntryOrLeavingPoint.Add(finalMapPoint, false);
        //    }

        //    MapRegion GetAGVCurrentRegion()
        //    {
        //        return Agv.currentMapPoint.GetRegion(CurrentMap);
        //    }

        //    int _sequence = 0;


        //    foreach (var keypair in checkPointIsRegionEntryOrLeavingPoint)
        //    {
        //        var checkPoint = keypair.Key;
        //        bool _isCurrentGoalIsEntryOrLeavePtOfRegion = keypair.Value;
        //        //if (_isCurrentGoalIsEntryOrLeavePtOfRegion)
        //        //{
        //        //    StaMap.RegistPoint(Agv.Name, checkPoint, out var msg);
        //        //}
        //        var _subGaol = _tempGoal = NextCheckPoint = checkPoint;
        //        bool _isAagvAlreadyThereBegin = Agv.states.Last_Visited_Node == _tempGoal.TagNumber;
        //        bool _isTurningAngleDoneInNarrow = false;

        //        var _currentRegion = GetAGVCurrentRegion();


        //        bool _isCurrentRegionNarrow = _currentRegion != null && _currentRegion.IsNarrowPath;
        //        Stopwatch _searPathTimer = Stopwatch.StartNew();
        //        while (_isAagvAlreadyThereBegin || Agv.states.Last_Visited_Node != _subGaol.TagNumber && !IsTaskCanceled) //需考慮AGV已經在目的地
        //        {

        //            if (IsTaskCanceled)
        //                throw new TaskCanceledException();

        //            (bool success, IEnumerable<MapPoint> optimizePath, clsPathSearchResult results) result = new(false, null, new clsPathSearchResult());
        //            IEnumerable<MapPoint> nextOptimizePath = new List<MapPoint>();

        //            if (_isAagvAlreadyThereBegin)
        //            {
        //                nextOptimizePath = new List<MapPoint> { Agv.currentMapPoint };

        //            }
        //            else
        //            {
        //                _searPathTimer.Restart();

        //                if (_isCurrentRegionNarrow && finalGoalRegion.IsNarrowPath || _isCurrentGoalIsEntryOrLeavePtOfRegion)
        //                    _tempGoal = finalMapPoint;
        //                while (!(result = await _SearchPassablePath(_tempGoal, new List<MapPoint>())).success)
        //                {

        //                    RealTimeOptimizePathSearchReuslt = result.optimizePath;
        //                    //if (_searPathTimer.Elapsed.Seconds > 5)
        //                    //{
        //                    //    PathConflicRequest.CONFLIC_STATE conflicReason = result.results.IsConflicByNarrowPathDirection ?
        //                    //        PathConflicRequest.CONFLIC_STATE.NARROW_PATH_CONFLIC : PathConflicRequest.CONFLIC_STATE.REMAIN_PATH_COLLUSION_CONFLIC;
        //                    //    PathConflicSolveRequestInvoke(new TrafficControlCenter.PathConflicRequest(Agv,
        //                    //        result.results.ConflicAGVCollection.Distinct(),
        //                    //        result.optimizePath,
        //                    //        conflicReason));
        //                    //    return;
        //                    //}

        //                    if (IsTaskCanceled || disposedValue)
        //                        throw new TaskCanceledException();

        //                    //if (result.results.IsConflicByNarrowPathDirection && !_isTurningAngleDoneInNarrow)
        //                    //{
        //                    //    _isTurningAngleDoneInNarrow = await HandleAGVAtNarrowPath(_sequence, _isTurningAngleDoneInNarrow, result);
        //                    //    await Task.Delay(1000);
        //                    //    continue;

        //                    //}

        //                    TrafficWaitingState.SetDisplayMessage($"(Search Path to Tag-{_tempGoal.TagNumber}...)");

        //                    await Task.Delay(1000);
        //                    try
        //                    {
        //                        //取出下一個停止點

        //                        if (_isCurrentRegionNarrow && finalGoalRegion.IsNarrowPath || _isCurrentGoalIsEntryOrLeavePtOfRegion)
        //                        {
        //                            _tempGoal = finalMapPoint;
        //                            continue;
        //                        }

        //                        var othersAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv);
        //                        var otherAGVPoints = othersAGV.Select(agv => agv.currentMapPoint);
        //                        var filterVirtualPoints = result.optimizePath.Where(pt => pt.TagNumber != Agv.currentMapPoint.TagNumber)
        //                                                                     .Where(pt => !pt.IsVirtualPoint) //濾除虛擬點
        //                                                                     .Where(pt => !othersAGV.Any(agv => agv.AGVRotaionGeometry.IsIntersectionTo(Agv.AGVRotaionGeometry)))
        //                                                                     .Where(pt => otherAGVPoints.All(agv_pt => agv_pt.CalculateDistance(pt) >= 2));//距離其他車輛2公尺以上
        //                        filterVirtualPoints = filterVirtualPoints.Reverse();
        //                        // filterVirtualPoints.Count() < 2;
        //                        int candicatPointsNum = filterVirtualPoints.Count();
        //                        _tempGoal = filterVirtualPoints.Skip(candicatPointsNum > 2 ? 2 : candicatPointsNum - 1).First();

        //                    }
        //                    catch (Exception ex)
        //                    {
        //                        _tempGoal = finalMapPoint;
        //                        continue;
        //                    }

        //                }
        //                RealTimeOptimizePathSearchReuslt = nextOptimizePath = result.optimizePath;

        //                //[Loacal Method]Low Level Search
        //                async Task<(bool success, IEnumerable<MapPoint> optimizePath, clsPathSearchResult results)> _SearchPassablePath(MapPoint goal, IEnumerable<MapPoint> _constrains)
        //                {
        //                    clsPathSearchResult _searchResult = new clsPathSearchResult();
        //                    bool PassableInNarrowPath(out IEnumerable<IAGV> conflicAGVCollection)
        //                    {
        //                        conflicAGVCollection = new List<IAGV>();

        //                        if (!Agv.currentMapPoint.IsNarrowPath)
        //                            return true;
        //                        var nonHorizontalAGVs = VMSManager.AllAGV.FilterOutAGVFromCollection(Agv)
        //                                                .Where(_agv => _agv.currentMapPoint.IsNarrowPath)
        //                                                .Where(_agv => !_agv.IsDirectionHorizontalTo(Agv));
        //                        conflicAGVCollection = nonHorizontalAGVs;
        //                        return !nonHorizontalAGVs.Any();
        //                    }

        //                    var optimizePath = LowLevelSearch.GetOptimizedMapPoints(this.Agv.currentMapPoint, goal);
        //                    bool isPathHasPointsBeRegisted = optimizePath.IsPathHasPointsBeRegisted(this.Agv, out var registed);
        //                    bool isHasAnyYieldPoints = optimizePath.IsPathHasAnyYieldingPoints(out var yieldPoints);
        //                    _searchResult.isPathConflicByAGVGeometry = optimizePath.IsPathConflicWithOtherAGVBody(this.Agv, out var conflicAGVListOfPathCollsion);
        //                    _searchResult.IsConflicByNarrowPathDirection = !PassableInNarrowPath(out IEnumerable<IAGV> conflicNarrowPathAGVCollection) && _searchResult.isPathConflicByAGVGeometry;

        //                    List<IAGV> conflicAGVs = new List<IAGV>();
        //                    conflicAGVs.AddRange(conflicAGVListOfPathCollsion);
        //                    conflicAGVs.AddRange(conflicNarrowPathAGVCollection);
        //                    _searchResult.ConflicAGVCollection = conflicAGVs;

        //                    bool HasOtherNewPath = false;
        //                    bool IsPathPassable = !isPathHasPointsBeRegisted && !_searchResult.isPathConflicByAGVGeometry && !_searchResult.IsConflicByNarrowPathDirection;

        //                    IEnumerable<MapPoint> secondaryPath = new List<MapPoint>();
        //                    if (!IsPathPassable)
        //                    {
        //                        List<MapPoint> constrains = new List<MapPoint>();
        //                        var othersAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(Agv);
        //                        var otherAgvPoints = othersAGV.Select(agv => agv.currentMapPoint);
        //                        constrains.AddRange(otherAgvPoints);

        //                        IEnumerable<MapPoint> AgvConflicAround(IAGV _agv)
        //                        {
        //                            return _agv.currentMapPoint.TargetNormalPoints()
        //                                                .Where(pt => pt.GetCircleArea(ref _agv)
        //                                                .IsIntersectionTo(_agv.AGVRotaionGeometry));
        //                        }



        //                        constrains.AddRange(othersAGV.Where(agv => agv.currentMapPoint.IsCharge).SelectMany(agv => agv.currentMapPoint.TargetNormalPoints()));

        //                        //constrains.AddRange(othersAGV.SelectMany(agv => AgvConflicAround(agv)));
        //                        constrains = constrains.Where(pt => pt.TagNumber != Agv.currentMapPoint.TagNumber)
        //                                               .Where(pt => pt.TagNumber != optimizePath.Last().TagNumber)
        //                                               .ToList();

        //                        var OtherNewPathFound = LowLevelSearch.TryGetOptimizedMapPointWithConstrains(ref optimizePath, constrains, out secondaryPath);
        //                        bool isPathconflicOfSecondaryPath = secondaryPath.IsPathConflicWithOtherAGVBody(this.Agv, out conflicAGVListOfPathCollsion);
        //                        bool isPathPtRegistedOfSecondaryPath = secondaryPath.IsPathHasPointsBeRegisted(this.Agv, out registed);
        //                        _searchResult.isPathConflicByAGVGeometry = isPathconflicOfSecondaryPath;
        //                        _searchResult.IsConflicByNarrowPathDirection = !PassableInNarrowPath(out conflicNarrowPathAGVCollection) && _searchResult.isPathConflicByAGVGeometry;


        //                        conflicAGVs.Clear();
        //                        conflicAGVs.AddRange(conflicAGVListOfPathCollsion);
        //                        conflicAGVs.AddRange(conflicNarrowPathAGVCollection);
        //                        _searchResult.ConflicAGVCollection = conflicAGVs;


        //                        if (OtherNewPathFound && !isPathPtRegistedOfSecondaryPath)
        //                        {

        //                            return (true, secondaryPath, _searchResult);
        //                        }

        //                    }
        //                    return (IsPathPassable && optimizePath.Last() != Agv.currentMapPoint, optimizePath, _searchResult);
        //                }
        //            }

        //            _isTurningAngleDoneInNarrow = false;
        //            var nextPath = GetNextPath(new clsPathInfo
        //            {
        //                stations = nextOptimizePath.ToList(),
        //            }, Agv.states.Last_Visited_Node, out bool IsNexPathHasEqReplcingParts, out int blockByEqPartsReplace, 4);

        //            await NarrowDirectionCheck(nextPath);

        //            var isNextPathGoalIsFinal = nextPath.Last() == _subGaol;


        //            double stopAngle = nextPath.GetStopDirectionAngle(OrderData, Agv, Stage, nextPath.Last());
        //            var pathToAGVSegment = PathFinder.GetTrajectory(StaMap.Map.Name, nextPath.ToList()).ToArray();
        //            pathToAGVSegment = pathToAGVSegment.SkipWhile(pt => _previsousTrajectorySendToAGV.Any(_pt => _pt.Point_ID == pt.Point_ID)).ToArray();
        //            int nearGoalTag = 0;
        //            if (pathToAGVSegment.Count() != 0)
        //            {
        //                _previsousTrajectorySendToAGV.AddRange(pathToAGVSegment);

        //                //產生丟給車載的數據模型
        //                clsTaskDownloadData _taskDownloadData = new clsTaskDownloadData
        //                {
        //                    Task_Name = OrderData.TaskName,
        //                    Task_Sequence = _sequence,
        //                    Action_Type = ACTION_TYPE.None,
        //                    Destination = finalMapPoint.TagNumber,
        //                };
        //                _taskDownloadData.Trajectory = _previsousTrajectorySendToAGV.ToArray();
        //                _taskDownloadData.Trajectory.Last().Theta = stopAngle;

        //                MoveTaskEvent = new clsMoveTaskEvent(Agv, nextOptimizePath.GetTagCollection(), nextPath.ToList(), false);
        //                while (!StaMap.RegistPoint(Agv.Name, MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList, out string ErrorMessage))
        //                {
        //                    var regMsg = string.Join(",", MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList);
        //                    UpdateMoveStateMessage($"嘗試註冊點位中...{regMsg}");
        //                    await Task.Delay(1000);
        //                }

        //                await base._DispatchTaskToAGV(_taskDownloadData);
        //                _sequence += 1;
        //                nearGoalTag = nextPath.Reverse()
        //                                        .Skip(isNextPathGoalIsFinal || nextPath.Count() <= 2 ? 0 : 1)
        //                                        .FirstOrDefault().TagNumber;
        //                UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(前往 Tag-{nearGoalTag}->{nextPath.Last().TagNumber})");
        //            }
        //            else
        //            {
        //                nearGoalTag = _previsousTrajectorySendToAGV.Last().Point_ID;
        //            }


        //            while (!PassedTags.Contains(nearGoalTag))
        //            {

        //                if (IsTaskCanceled)
        //                    throw new TaskCanceledException();

        //                //if (anyAgvRemainPathWillConflicWhenAgvRotating(nextPath))
        //                //{
        //                //    UpdateMoveStateMessage($"Imapcted  State Detected");
        //                //    continue;
        //                //}

        //                if (anyAgvInSameNarrowRegionButNotHorizon())
        //                {
        //                    UpdateMoveStateMessage($"Non-horizon State Detected");
        //                    continue;
        //                }
        //                else
        //                {
        //                    if (_isAagvAlreadyThereBegin)
        //                        break;
        //                    if (nearGoalTag == DestineTag)
        //                    {
        //                        while (Agv.states.Last_Visited_Node != DestineTag)
        //                        {
        //                            UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(即將抵達終點-{DestineTag})");
        //                            await Task.Delay(1);
        //                            if (IsTaskCanceled || disposedValue)
        //                                throw new TaskCanceledException();
        //                        }
        //                        return;
        //                    }
        //                    else
        //                    {
        //                        if (Agv.states.Last_Visited_Node == nearGoalTag || nextPath.Last().TagNumber == Agv.states.Last_Visited_Node)
        //                            break;
        //                    }
        //                }


        //                await Task.Delay(100);
        //            }
        //            _isAagvAlreadyThereBegin = false;
        //            _tempGoal = _subGaol;
        //            bool reachDesine = checkPoint.TagNumber == DestineTag;

        //            await Task.Delay(1);



        //        }
        //        string GetDestineDisplay()
        //        {
        //            int _destineTag = 0;
        //            bool isCarryOrderAndGoToSource = OrderData.Action == ACTION_TYPE.Carry && Stage == VehicleMovementStage.Traveling_To_Source;
        //            _destineTag = isCarryOrderAndGoToSource ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;
        //            return StaMap.GetStationNameByTag(_destineTag);
        //        }
        //    }

        //    bool anyAgvRemainPathWillConflicWhenAgvRotating(IEnumerable<MapPoint> _nexPath)
        //    {
        //        if (!_nexPath.Any())
        //            return false;

        //        List<MapPoint> _NextPath = new List<MapPoint>() { };
        //        if (_nexPath.Count() == 1)  //原地旋轉
        //        {
        //            _NextPath = new List<MapPoint> { Agv.currentMapPoint };
        //        }
        //        else
        //        {
        //            var strtPt = _nexPath.ToList()[0];
        //            var nextPt = _nexPath.ToList()[1];
        //            _NextPath = new List<MapPoint>
        //            {
        //                strtPt,nextPt
        //            };
        //        }
        //        var otherExecutingOrderAGv = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv).Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
        //        if (!otherExecutingOrderAGv.Any())
        //            return false;
        //        return otherExecutingOrderAGv.Where(agv => agv.CurrentRunningTask().RealTimeOptimizePathSearchReuslt.IsRemainPathConflicWithOtherAGVBody(agv, out var conflicsAgv))
        //                                     .Any();
        //    }

        //    bool anyAgvInSameNarrowRegionButNotHorizon()
        //    {
        //        var agvRegion = Agv.currentMapPoint.GetRegion(CurrentMap);
        //        if (agvRegion == null || agvRegion.Name == "" || !agvRegion.IsNarrowPath)
        //            return false;
        //        return GetInRegionVehicles().Any(v => !v.IsDirectionHorizontalTo(Agv));
        //    }
        //    async Task NarrowDirectionCheck(IEnumerable<MapPoint> _nextPath)
        //    {
        //        var currentRegion = GetAGVCurrentRegion();
        //        if (currentRegion == null || !currentRegion.IsNarrowPath)
        //            return;
        //        if (Agv.IsDirectionIsMatchToRegionSetting(out double regionSetting, out double diff) && !_AnyAGVInRegionNotCorrectDirection())
        //            return;

        //        var _finalStopTheta = optimizePathGolbal.GetStopDirectionAngle(OrderData, Agv, Stage, optimizePathGolbal.Last());
        //        bool isAGVAtDestine = Agv.states.Coordination.Theta != _finalStopTheta && Agv.currentMapPoint.TagNumber == finalMapPoint.TagNumber;
        //        if (isAGVAtDestine)
        //            return;

        //        //計算夾角


        //        var nextGaolPt = _nextPath.FirstOrDefault(pt => pt.TagNumber != Agv.currentMapPoint.TagNumber);



        //        if (nextGaolPt != null)
        //        {

        //            var distanceToNextGoal = Agv.currentMapPoint.CalculateDistance(nextGaolPt);
        //            var positionOfOtherAGV = GetInRegionVehicles().ToDictionary(agv => agv, agv => Agv.currentMapPoint.CalculateDistance(agv.currentMapPoint) <= distanceToNextGoal);
        //            if (positionOfOtherAGV.Values.All(v => v == true))
        //                return;
        //        }
        //        async Task<bool> WaitAGVIDLE()
        //        {
        //            while (Agv.main_state != clsEnums.MAIN_STATUS.IDLE)
        //            {
        //                await Task.Delay(100);
        //            }
        //            return true;
        //        }

        //        async Task<bool> WaitAGVRUN()
        //        {
        //            while (Agv.main_state != clsEnums.MAIN_STATUS.RUN)
        //            {
        //                await Task.Delay(100);
        //            }
        //            return true;
        //        }

        //        if (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
        //        {
        //            await WaitAGVIDLE();
        //        }
        //        await SendCancelRequestToAGV();

        //        double stopAngle = regionSetting;
        //        if (regionSetting == 90)
        //        {
        //            if (Agv.states.Coordination.Theta >= 0 && Agv.states.Coordination.Theta <= 180)
        //            {
        //                stopAngle = regionSetting;
        //            }
        //            else
        //            {

        //                stopAngle = regionSetting - 180;
        //            }

        //        }
        //        else if (regionSetting == 0)
        //        {
        //            if (Agv.states.Coordination.Theta >= -90 && Agv.states.Coordination.Theta <= 90)
        //            {
        //                stopAngle = regionSetting;
        //            }
        //            else
        //            {

        //                stopAngle = regionSetting - 180;
        //            }
        //        }

        //        await _DispatchTaskToAGV(new clsTaskDownloadData
        //        {
        //            Task_Name = OrderData.TaskName,
        //            Task_Sequence = _sequence,
        //            Action_Type = ACTION_TYPE.None,
        //            Destination = Agv.currentMapPoint.TagNumber,
        //            Trajectory = new clsMapPoint[1] { new clsMapPoint{
        //                                Point_ID = Agv.currentMapPoint.TagNumber,
        //                                Theta = stopAngle,
        //                                X = Agv.currentMapPoint.X,
        //                                Y = Agv.currentMapPoint.Y,
        //                                Laser =  5
        //                            } }
        //        });

        //        while (Agv.main_state == clsEnums.MAIN_STATUS.RUN || !Agv.IsDirectionIsMatchToRegionSetting(out regionSetting, out diff))
        //        {
        //            await Task.Delay(1000);
        //            UpdateMoveStateMessage($"Wait Direction Correct..({regionSetting})");
        //            if (IsTaskCanceled)
        //                throw new TaskCanceledException();
        //        }
        //        await WaitAGVIDLE();
        //        _previsousTrajectorySendToAGV.Clear();


        //        while (_AnyAGVInRegionNotCorrectDirection())
        //        {
        //            if (IsTaskCanceled)
        //                throw new TaskCanceledException();
        //            UpdateMoveStateMessage($"Wait Direction Check...");
        //            await Task.Delay(1000);
        //        }
        //    }

        //    IEnumerable<IAGV> GetInRegionVehicles()
        //    {
        //        var currentRegion = GetAGVCurrentRegion();
        //        if (currentRegion == null || currentRegion.Name == null || currentRegion.Name == "")
        //            return new List<IAGV>();
        //        return VMSManager.AllAGV.FilterOutAGVFromCollection(Agv)
        //                               .Where(agv => agv.currentMapPoint.GetRegion(CurrentMap)?.Name == currentRegion.Name);
        //    }

        //    bool _AnyAGVInRegionNotCorrectDirection()
        //    {
        //        return GetInRegionVehicles().Where(agv => !agv.IsDirectionIsMatchToRegionSetting(out _, out _)).Any();
        //    }


        //    //return base.SendTaskToAGV();
        //}

        public int SeqIndex = 0;

        List<MapPoint> dynamicConstrains = new List<MapPoint>();

        public override async Task SendTaskToAGV()
        {
            MapPoint finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
            DestineTag = finalMapPoint.TagNumber;
            _previsousTrajectorySendToAGV = new List<clsMapPoint>();
            int _seq = 0;
            while (_seq == 0 || DestineTag != Agv.currentMapPoint.TagNumber)
            {
                await Task.Delay(100);
                if (IsTaskCanceled || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                    throw new TaskCanceledException();
                try
                {
                    var dispatchCenterReturnPath = (await DispatchCenter.MoveToDestineDispatchRequest(Agv, OrderData, Stage));
                    if (dispatchCenterReturnPath == null)
                    {
                        UpdateMoveStateMessage("Search Path...");
                        await Task.Delay(1000);
                        continue;
                    }
                    var nextPath = dispatchCenterReturnPath.ToList();
                    TrafficWaitingState.SetStatusNoWaiting();
                    var nextGoal = nextPath.Last();
                    StaMap.RegistPoint(Agv.Name, nextPath, out var msg);
                    nextPath.First().Direction = int.Parse(Math.Round(Agv.states.Coordination.Theta) + "");
                    var trajectory = PathFinder.GetTrajectory(CurrentMap.Name, nextPath.ToList());
                    trajectory = trajectory.Where(pt => !_previsousTrajectorySendToAGV.GetTagList().Contains(pt.Point_ID)).ToArray();
                    _previsousTrajectorySendToAGV.AddRange(trajectory);
                    _previsousTrajectorySendToAGV = _previsousTrajectorySendToAGV.Distinct().ToList();
                    trajectory.Last().Theta = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, nextPath.Last());

                    await _DispatchTaskToAGV(new clsTaskDownloadData
                    {
                        Action_Type = ACTION_TYPE.None,
                        Task_Name = OrderData.TaskName,
                        Destination = DestineTag,
                        Trajectory = _previsousTrajectorySendToAGV.ToArray(),
                        Task_Sequence = _seq
                    });
                    _seq += 1;
                    MoveTaskEvent = new clsMoveTaskEvent(Agv, nextPath.GetTagCollection(), nextPath.ToList(), false);
                    UpdateMoveStateMessage($"Go to {nextGoal.TagNumber}");
                    while (nextGoal.TagNumber != Agv.currentMapPoint.TagNumber)
                    {
                        if (IsTaskCanceled || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                            throw new TaskCanceledException();
                        UpdateMoveStateMessage($"Go to {nextGoal.TagNumber}");
                        await Task.Delay(100);
                    }
                    var remainPath = nextPath.Where(pt => nextPath.IndexOf(nextGoal) >= nextPath.IndexOf(nextGoal));

                    Agv.NavigationState.UpdateNavigationPoints(remainPath);

                    _ = Task.Run(async () =>
                    {
                        UpdateMoveStateMessage($"Reach{nextGoal.TagNumber}!");
                        await Task.Delay(1000);
                        TrafficWaitingState.SetStatusNoWaiting();
                        DispatchCenter.CancelDispatchRequest(Agv);
                    });
                }
                catch (Exception ex)
                {
                    throw ex;
                }

            }

            //try
            //{

            //    var _dynamicConstrains = StaMap.RegistDictionary.Where(pair => pair.Value.RegisterAGVName != Agv.Name)
            //                                               .Select(pair => StaMap.GetPointByTagNumber(pair.Key)).ToList();
            //    dynamicConstrains.AddRange(_dynamicConstrains);
            //    dynamicConstrains = dynamicConstrains.Distinct().ToList();
            //    SeqIndex = 0;
            //    MapPoint finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
            //    MapRegion finalGoalRegion = finalMapPoint.GetRegion(CurrentMap);
            //    DestineTag = finalMapPoint.TagNumber;

            //    IEnumerable<MapPoint> subGoals = GetGoalsOfOptimizePath(finalMapPoint, dynamicConstrains);
            //    List<clsMapPoint> _sendTrajectory = new List<clsMapPoint>();

            //    if (subGoals.Count() == 0)
            //    {
            //        return;
            //    }

            //    foreach (MapPoint subGoal in subGoals)
            //    {
            //        IEnumerable<MapPoint> subOptizePathPlanToSubGoal = new List<MapPoint>();

            //        try
            //        {
            //            subOptizePathPlanToSubGoal = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, subGoal, dynamicConstrains);
            //        }
            //        catch (Exception ex)
            //        {
            //            dynamicConstrains.Add(subGoal);
            //            SendTaskToAGV();
            //            return;
            //        }
            //        while (!IsPathMovable(subOptizePathPlanToSubGoal, out List<MapPoint> NotReachablePoints))
            //        {

            //            if (IsTaskCanceled)
            //                throw new TaskCanceledException();

            //            //if(subOptizePathPlanToSubGoal.Count()==1 && subOptizePathPlanToSubGoal.Last()== Agv.currentMapPoint)
            //            //{
            //            //    break;
            //            //}
            //            UpdateMoveStateMessage($"Wait {string.Join(",", NotReachablePoints.GetTagCollection())} Passable..");
            //            await Task.Delay(1000);
            //            dynamicConstrains = NotReachablePoints.Where(pt => pt != Agv.currentMapPoint && pt != subGoal).ToList();
            //            var remainPassblePoints = subOptizePathPlanToSubGoal.Where(pt => !pt.IsVirtualPoint && !NotReachablePoints.Contains(pt));
            //            foreach (var remainSubGoal in remainPassblePoints.Reverse())
            //            {
            //                try
            //                {
            //                    var _remainOptizePathPlanToNearSubGoal = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, subGoal, dynamicConstrains);
            //                    if (IsPathMovable(_remainOptizePathPlanToNearSubGoal, out var _))
            //                    {
            //                        subOptizePathPlanToSubGoal = _remainOptizePathPlanToNearSubGoal;
            //                        break;
            //                    }
            //                }
            //                catch (Exception)
            //                {

            //                }
            //            }
            //        }

            //        while (!StaMap.RegistPoint(this.Agv.Name, subOptizePathPlanToSubGoal, out var msg))
            //        {
            //            if (IsTaskCanceled)
            //                throw new TaskCanceledException();
            //            UpdateMoveStateMessage($"Wait {string.Join(",", subOptizePathPlanToSubGoal.GetTagCollection())} Registable..");
            //            await Task.Delay(1000);
            //        };

            //        clsTaskDownloadData _taskDownloadToVCS = new clsTaskDownloadData
            //        {
            //            Action_Type = ACTION_TYPE.None,
            //            Task_Name = OrderData.TaskName,
            //            Task_Sequence = SeqIndex,
            //            Destination = DestineTag,
            //            //Trajectory = PathFinder.GetTrajectory(CurrentMap.Name, subOptizePathPlanToSubGoal.ToList())
            //        };
            //        var trjactoryFormCurrentPotinToSubGoal = PathFinder.GetTrajectory(CurrentMap.Name, subOptizePathPlanToSubGoal.ToList());
            //        var stopAngle = subOptizePathPlanToSubGoal.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, subGoal);
            //        RealTimeOptimizePathSearchReuslt = subOptizePathPlanToSubGoal;
            //        _sendTrajectory.AddRange(trjactoryFormCurrentPotinToSubGoal.Where(pt => !_sendTrajectory.GetTagList().Contains(pt.Point_ID)));
            //        _taskDownloadToVCS.Trajectory = _sendTrajectory.ToArray();
            //        _taskDownloadToVCS.Trajectory.Last().Theta = stopAngle;
            //        _sendTrajectory = _taskDownloadToVCS.Trajectory.ToList();

            //        var taskDownloadResult = await _DispatchTaskToAGV(_taskDownloadToVCS);
            //        if (taskDownloadResult.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
            //        {
            //            throw new Exception("Task Download to AGV Fail");
            //        }
            //        SeqIndex += 1;
            //        UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(前往 Tag-{subGoal.TagNumber})");
            //        await WaitReachGoal(subGoal);

            //    }


            //    bool IsPathMovable(IEnumerable<MapPoint> path, out List<MapPoint> forbidPoints)
            //    {
            //        forbidPoints = new List<MapPoint>();
            //        bool isAnyPtRegisted = path.IsPathHasPointsBeRegisted(Agv, out var registedPoints);
            //        IAGV thisAGV = this.Agv;
            //        var currentRegion = this.Agv.currentMapPoint.GetRegion(CurrentMap);

            //        bool isInNarrowRegion = currentRegion != null && currentRegion.IsNarrowPath;

            //        var pathRegion = path.GetPathRegion(this.Agv, isInNarrowRegion ? 0.3 : 0, 0);
            //        // IEnumerable<MapPoint> conflicPoints = path.Where(point => OtherAGV.Any(agv => agv.AGVRotaionGeometry.IsIntersectionTo(point.GetCircleArea(ref thisAGV))));
            //        var conflicPathes = pathRegion.Where(rect => OtherAGV.Any(agv => agv.AGVGeometery.IsIntersectionTo(rect)))
            //                                       .Where(rect => rect.StartPointTag == Agv.currentMapPoint);
            //        var conflicPointsWithGeometry = path.Where(pt => conflicPathes.Any(rect => rect.EndPointTag.TagNumber == pt.TagNumber));
            //        forbidPoints.AddRange(conflicPointsWithGeometry);
            //        forbidPoints = forbidPoints.Distinct().ToList();
            //        return forbidPoints.Count == 0;
            //    }
            //    string GetDestineDisplay()
            //    {
            //        int _destineTag = 0;
            //        bool isCarryOrderAndGoToSource = OrderData.Action == ACTION_TYPE.Carry && Stage == VehicleMovementStage.Traveling_To_Source;
            //        _destineTag = isCarryOrderAndGoToSource ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;
            //        return StaMap.GetStationNameByTag(_destineTag);
            //    }

            //    async Task WaitReachGoal(MapPoint subGoal)
            //    {
            //        while (Agv.currentMapPoint.TagNumber != subGoal.TagNumber)
            //        {
            //            if (IsTaskCanceled)
            //                throw new TaskCanceledException();

            //            //if (subGoal != finalMapPoint && subGoal.CalculateDistance(Agv.states.Coordination.X, Agv.states.Coordination.Y) <= 1)
            //            //{
            //            //    break;
            //            //}
            //            await Task.Delay(100);
            //        }
            //        UpdateMoveStateMessage($"[{OrderData.ActionName}]-終點:{GetDestineDisplay()}\r\n(抵達 Tag-{subGoal.TagNumber})");
            //    }

            //}
            //catch (TaskCanceledException ex)
            //{
            //    throw ex;
            //}
            //catch (Exception ex)
            //{
            //    throw ex;
            //}


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
            public static IEnumerable<MapPoint> GetOptimizedMapPoints(MapPoint StartPoint, MapPoint GoalPoint, IEnumerable<MapPoint> constrains)
            {
                PathFinder _pathFinder = new PathFinder();
                clsPathInfo _pathInfo = _pathFinder.FindShortestPath(_Map, StartPoint, GoalPoint, new PathFinderOption
                {
                    OnlyNormalPoint = true,
                    ConstrainTags = constrains.GetTagCollection().ToList()
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
            if (path.Count() == 1)
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

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Linq;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.Dispatch;
using VMSystem.Dispatch.Regions;
using VMSystem.Dispatch.YieldActions;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;
using static AGVSystemCommonNet6.DATABASE.DatabaseCaches;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.TrafficControl.VehicleNavigationState;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    /// <summary>
    /// 0428 動態路徑生程規劃開發
    /// </summary>
    public partial class MoveTaskDynamicPathPlanV2 : MoveTaskDynamicPathPlan
    {
        public MapPoint finalMapPoint { get; private set; }
        public MoveTaskDynamicPathPlanV2() : base()
        {
        }
        public MoveTaskDynamicPathPlanV2(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
            StaMap.OnPointsDisabled += HandlePointsChangeToDisabled;
        }


        public override void CreateTaskToAGV()
        {
            //base.CreateTaskToAGV();
        }
        public override bool IsAGVReachDestine => Agv.states.Last_Visited_Node == DestineTag;

        public enum SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY
        {
            ANY,
            SELECT_NO_BLOCKED_PATH_POINT,
            FOLLOWING,
            SAME_REGION
        }

        public class clsPathSearchResult
        {
            public bool IsConflicByNarrowPathDirection { get; set; }
            public bool isPathConflicByAGVGeometry { get; set; }

            public IEnumerable<IAGV> ConflicAGVCollection { get; set; }
        }
        public int SeqIndex = 0;

        List<MapPoint> dynamicConstrains = new List<MapPoint>();

        private async void HandlePointsChangeToDisabled(object? sender, List<MapPoint> disabledPoints)
        {
            await Task.Delay(1);
            var disabledTags = disabledPoints.GetTagCollection();
            var blockedPointInRemainPath = Agv.NavigationState.NextNavigtionPoints.Where(pt => disabledTags.Contains(pt.TagNumber)).ToList();
            bool IsRemainPathBeDisable = blockedPointInRemainPath.Any();
            if (!IsRemainPathBeDisable)
                return;
            await CycleStopRequestAsync();
        }
        public override async Task SendTaskToAGV()
        {
            this.parentTaskBase = this;
            //StartRecordTrjectory();
            Agv.NavigationState.IsWaitingConflicSolve = false;
            cycleStopRequesting = false;
            Agv.NavigationState.IsWaitingForLeaveWorkStationTimeout = false;
            Agv.OnMapPointChanged += Agv_OnMapPointChanged;
            bool IsRegionNavigationEnabled = TrafficControlCenter.TrafficControlParameters.Basic.MultiRegionNavigation;
            try
            {
                finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);

                if (finalMapPoint == null)
                {
                    throw new NoPathForNavigatorException();
                }

                DestineTag = finalMapPoint.TagNumber;
                _previsousTrajectorySendToAGV = new List<clsMapPoint>();
                int _seq = 0;
                if (Stage != VehicleMovementStage.AvoidPath && Stage != VehicleMovementStage.AvoidPath_Park)
                    Agv.NavigationState.StateReset();

                if (IsRegionNavigationEnabled && Stage != VehicleMovementStage.Traveling_To_Region_Wait_Point && IsPathPassMuiltRegions(finalMapPoint, out List<MapRegion> regions))
                {
                    await RegionPathNavigation(regions);
                }

                MapPoint searchStartPt = Agv.currentMapPoint.Clone();
                Stopwatch pathConflicStopWatch = new Stopwatch();
                pathConflicStopWatch.Start();
                bool _IsFinalThetaCorrect = true;
                double finalThetaCheck = 0;

                while (_seq == 0 || DestineTag != Agv.currentMapPoint.TagNumber)
                {
                    await Task.Delay(10);
                    if (IsTaskAborted())
                        throw new TaskCanceledException();
                    try
                    {
                        Agv.NavigationState.currentConflicToAGV = null;
                        Agv.NavigationState.CurrentConflicRegion = null;

                        var dispatchCenterReturnPath = (await DispatchCenter.MoveToDestineDispatchRequest(Agv, searchStartPt, OrderData, Stage));

                        if (dispatchCenterReturnPath == null || !dispatchCenterReturnPath.Any() || Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                        {
                            if (Stage == VehicleMovementStage.AvoidPath)
                            {
                                NotifyServiceHelper.ERROR($"{Agv.Name} 避車至 {finalMapPoint.Graph.Display} 失敗.");
                                if (await TryRaiseConflicAGVAvoid(Agv.NavigationState.currentConflicToAGV))
                                {
                                    NotifyServiceHelper.ERROR($"{Agv.Name} 避車失敗,當前避讓車為 {Agv.NavigationState.currentConflicToAGV.Name}");
                                    await Task.Delay(3000);
                                    return;
                                }
                                Agv.NavigationState.AddCannotReachPointWhenAvoiding(finalMapPoint);
                                await Task.Delay(1000);
                                UpdateMoveStateMessage($"Search Path...");
                            }

                            pathConflicStopWatch.Start();
                            searchStartPt = Agv.currentMapPoint;
                            if (string.IsNullOrEmpty(TrafficWaitingState.Descrption))
                                UpdateMoveStateMessage($"Search Path...");
                            await Task.Delay(10);
                            bool _isConflicSolved = false;

                            if (pathConflicStopWatch.Elapsed.Seconds > 1 && !Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                            {
                                Agv.NavigationState.IsWaitingConflicSolve = true;
                            }

                            if (pathConflicStopWatch.Elapsed.Seconds > 3)
                            {
                                pathConflicStopWatch.Restart();
                                //動態移除當前衝突路線
                                DynamicClosePath();
                                continue;
                            }

                            if (Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                            {
                                pathConflicStopWatch.Stop();
                                pathConflicStopWatch.Reset();
                                await AvoidPathProcess(_seq);

                                if (this.parentTaskBase?.Stage == VehicleMovementStage.AvoidPath)
                                {
                                    return;
                                }

                                searchStartPt = Agv.currentMapPoint;
                                _previsousTrajectorySendToAGV.Clear();
                                await SendCancelRequestToAGV();
                                await Task.Delay(100);
                                Agv.OnMapPointChanged += Agv_OnMapPointChanged;
                            }
                            if (Agv.NavigationState.SpinAtPointRequest.IsSpinRequesting || Agv.NavigationState.SpinAtPointRequest.IsRaiseByAvoidingVehicleReqest)
                            {
                                await SpinAtCurrentPointProcess(_seq);
                            }
                            continue;
                        }
                        RestoreClosedPathes();
                        Agv.NavigationState.AvoidActionState.Reset();
                        Agv.NavigationState.IsWaitingConflicSolve = false;
                        pathConflicStopWatch.Stop();
                        pathConflicStopWatch.Reset();
                        var nextPath = dispatchCenterReturnPath.ToList();
                        TrafficWaitingState.SetStatusNoWaiting();
                        var nextGoal = nextPath.Last();
                        var remainPath = nextPath.Where(pt => nextPath.IndexOf(nextGoal) >= nextPath.IndexOf(nextGoal));
                        nextPath.First().Direction = int.Parse(Math.Round(Agv.states.Coordination.Theta) + "");
                        nextPath.Last().Direction = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, nextGoal);

                        var trajectory = PathFinder.GetTrajectory(CurrentMap.Name, nextPath.ToList());
                        trajectory = trajectory.Where(pt => !_previsousTrajectorySendToAGV.GetTagList().Contains(pt.Point_ID)).ToArray();

                        if (trajectory.Length == 0 && _IsFinalThetaCorrect)
                        {
                            searchStartPt = Agv.currentMapPoint;
                            continue;
                        }

                        //trajectory.Last().Theta = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, nextGoal);
                        _previsousTrajectorySendToAGV.AddRange(trajectory);
                        _previsousTrajectorySendToAGV = _previsousTrajectorySendToAGV.Distinct().ToList();

                        if (!StaMap.RegistPoint(Agv.Name, nextPath, out var msg))
                        {
                            await SendCancelRequestToAGV();
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            Agv.NavigationState.ResetNavigationPoints();
                            _previsousTrajectorySendToAGV.Clear();
                            searchStartPt = Agv.currentMapPoint;
                            continue;
                        }

                        Agv.NavigationState.IsWaitingConflicSolve = false;
                        Agv.NavigationState.UpdateNavigationPoints(nextPath);
                        await Task.Delay(100);
                        TaskExecutePauseMRE.WaitOne();
                        var responseOfVehicle = await _DispatchTaskToAGV(new clsTaskDownloadData
                        {
                            Action_Type = ACTION_TYPE.None,
                            Task_Name = OrderData.TaskName,
                            Destination = Agv.NavigationState.RegionControlState.State == REGION_CONTROL_STATE.WAIT_AGV_REACH_ENTRY_POINT ? nextGoal.TagNumber : DestineTag,
                            Trajectory = _previsousTrajectorySendToAGV.ToArray(),
                        });
                        if (responseOfVehicle.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                        {
                            throw new AGVRejectTaskException();
                        }
                        _seq += 1;
                        MoveTaskEvent = new clsMoveTaskEvent(Agv, nextPath.GetTagCollection(), nextPath.ToList(), false);
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
                                UpdateMoveStateMessage($"任務取消中...");
                                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                                throw new TaskCanceledException();
                            }

                            if (lastGoalTag == Agv.currentMapPoint.TagNumber && nextGoalTag != finalMapPoint.TagNumber)
                            {
                                break;
                            }

                            if (Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                                throw new TaskCanceledException();
                            if (cycleStopRequesting)
                            {
                                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                                cycleStopRequesting = false;
                                _previsousTrajectorySendToAGV.Clear();
                                searchStartPt = Agv.currentMapPoint;
                                break;
                            }
                            await Task.Delay(10);
                        }

                        if (Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion)
                        {
                            throw new RegionNotEnterableException();
                        }

                        if (nextGoalTag == finalMapPoint.TagNumber)
                        {
                            finalThetaCheck = nextPath.Last().Direction;

                        }
                        _ = Task.Run(async () =>
                        {
                            UpdateMoveStateMessage($"抵達-{nextGoal.Graph.Display}");
                            await Task.Delay(1000);
                        });

                        bool _willRotationFirst(double nextForwardAngle, out double error)
                        {
                            CalculateThetaError(Agv, nextForwardAngle, out error);
                            return error > 25;
                        }
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
                UpdateMoveStateMessage($"抵達-{finalMapPoint.Graph.Display}-等待停車完成..");

                if (Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion)
                {
                    Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
                    throw new RegionNotEnterableException();
                }
                else
                {

                    while (Agv.main_state == clsEnums.MAIN_STATUS.RUN && !IsTaskCanceled)
                    {
                        if (IsTaskCanceled)
                            throw new TaskCanceledException();
                        await Task.Delay(100);
                    }
                    if (IsTaskCanceled)
                        throw new TaskCanceledException();

                    UpdateMoveStateMessage($"抵達-{finalMapPoint.Graph.Display}-角度確認({finalThetaCheck})...");
                    await Task.Delay(100);

                    while (!CalculateThetaError(Agv, finalThetaCheck, out double error))
                    {
                        await FinalStopThetaAdjuctProcess();
                    }
                    UpdateMoveStateMessage($"抵達-{finalMapPoint.Graph.Display}-角度確認({finalThetaCheck}) OK!");
                    await Task.Delay(100);
                }

            }
            catch (RegionNotEnterableException ex)
            {
                logger.Warn("Region Not Enterable Event Occuring");
                await SendTaskToAGV();
            }
            catch (TaskCanceledException ex)
            {
                throw ex;
            }
            catch (AGVRejectTaskException ex)
            {
                throw ex;
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                EndReocrdTrajectory();
                TrafficWaitingState.SetStatusNoWaiting();
                Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
                Agv.NavigationState.StateReset();
            }


        }

        private void RestoreClosedPathes()
        {
            if (tempClosePathList.Any())
            {
                foreach (var path in tempClosePathList)
                {
                    StaMap.AddPathDynamic(path);
                }
                tempClosePathList.Clear();
            }
        }

        private List<MapPath> tempClosePathList = new List<MapPath>();
        private void DynamicClosePath()
        {
            if (Agv.NavigationState.CurrentConflicRegion == null)
                return;
            var startPtOfPathClose = Agv.NavigationState.CurrentConflicRegion.StartPoint;
            var endPtOfPathClose = Agv.NavigationState.CurrentConflicRegion.EndPoint;

            bool isSinglePointDetected = startPtOfPathClose.TagNumber == endPtOfPathClose.TagNumber;
            if (isSinglePointDetected)
            {
                //find all point has target to startPtOfPathClose
                var points = StaMap.Map.Points.Where(keypair => keypair.Value.TargetNormalPoints().Any(tpt => tpt.TagNumber == startPtOfPathClose.TagNumber))
                                              .Select(keypair => keypair.Value);
                if (points.Any())
                {
                    foreach (var pt in points)
                    {
                        RemovePath(pt, startPtOfPathClose);
                    }
                    return;
                }
            }

            RemovePath(startPtOfPathClose, endPtOfPathClose);
            void RemovePath(MapPoint startPt, MapPoint endPt)
            {
                if (StaMap.TryRemovePathDynamic(startPt, endPt, out MapPath path))
                {
                    NotifyServiceHelper.WARNING($"移除衝突路線-{path.ToString()}");
                    tempClosePathList.Add(path);
                }
            }

        }

        /// <summary>
        /// 嘗試請擋路的那一台車避讓
        /// </summary>
        /// <param name="currentConflicToAGV"></param>
        /// <returns></returns>
        private async Task<bool> TryRaiseConflicAGVAvoid(IAGV? currentConflicToAGV)
        {
            if (currentConflicToAGV == null)
                return false;
            bool isWaitMe = currentConflicToAGV.NavigationState.currentConflicToAGV?.Name == Agv.Name;

            clsLowPriorityVehicleMove clsLowPriorityVehicleMove = new clsLowPriorityVehicleMove(currentConflicToAGV, this.Agv);
            return await clsLowPriorityVehicleMove.StartSolve() != null;
        }

        private async Task<bool> WaitSpinDone(IAGV vehicle, double nextForwardAngle)
        {
            return CalculateThetaError(vehicle, nextForwardAngle, out double error);
        }
        private bool CalculateThetaError(IAGV vehicle, double finalThetaCheck, out double error)
        {
            double angleDifference = finalThetaCheck - vehicle.states.Coordination.Theta;
            if (angleDifference > 180)
                angleDifference -= 360;
            else if (angleDifference < -180)
                angleDifference += 360;
            error = Math.Abs(angleDifference);
            return error < 5;
        }

        private async Task FinalStopThetaAdjuctProcess()
        {
            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
            await _DispatchTaskToAGV(new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                Destination = finalMapPoint.TagNumber,
                Task_Name = OrderData.TaskName,
                Trajectory = new clsMapPoint[1] { _previsousTrajectorySendToAGV.Last() }
            });
            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
        }

        private async Task SpinAtCurrentPointProcess(int _seq)
        {
            double _forwardAngle = Agv.NavigationState.SpinAtPointRequest.ForwardAngle;

            if (CalculateThetaError(_forwardAngle, out _))
                return;


            LOG.TRACE($"{Agv.Name} 原地朝向角度修正任務-朝向角:[{_forwardAngle}] 度");

            _previsousTrajectorySendToAGV.Clear();
            List<MapPoint> _trajPath = new List<MapPoint>() {
                Agv.currentMapPoint.Clone()
            };
            _trajPath.Last().Direction = Agv.NavigationState.SpinAtPointRequest.ForwardAngle;
            clsMapPoint[] traj = PathFinder.GetTrajectory(CurrentMap.Name, _trajPath);
            _seq += 1;
            await CycleStopRequestAsync();
            await _DispatchTaskToAGV(new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                Destination = Agv.currentMapPoint.TagNumber,
                Trajectory = traj,
                Task_Name = OrderData.TaskName,
                Task_Sequence = _seq
            });

            while (!CalculateThetaError(_forwardAngle, out _) || Agv.main_state == clsEnums.MAIN_STATUS.RUN)
            {
                await Task.Delay(1000);
                UpdateMoveStateMessage($"Spin forward to {_forwardAngle}");
            }
            bool CalculateThetaError(double finalThetaCheck, out double error)
            {
                double angleDifference = finalThetaCheck - Agv.states.Coordination.Theta;
                if (angleDifference > 180)
                    angleDifference -= 360;
                else if (angleDifference < -180)
                    angleDifference += 360;
                error = Math.Abs(angleDifference);
                return error < 5;
            }
        }
        SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY WaitPointSelectStrategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.ANY;
        //summery this function 
        /// <summary>
        /// 進入管制區域時，選擇等待點策略
        /// </summary>
        /// <param name="regions"></param>
        /// <returns></returns>
        private async Task<bool> RegionPathNavigation(List<MapRegion> regions)
        {
            try
            {
                this.WaitPointSelectStrategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.ANY;
                if (regions.Count < 2)
                    return false;

                MapRegion? _Region = regions.Skip(1).FirstOrDefault(region => !RegionManager.IsRegionEnterable(Agv, region));
                if (_Region == null)
                    return false;

                TryGetWaitingPointSelectStregy(_Region, RegionManager.GetInRegionVehiclesNames(_Region), out SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY WaitPointSelectStrategy);

                if (WaitPointSelectStrategy == SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SAME_REGION)
                {
                    this.WaitPointSelectStrategy = WaitPointSelectStrategy;
                    NotifyServiceHelper.INFO($"同區域-[{_Region.Name}] 衝突!!");
                    return false;
                }

                if (WaitPointSelectStrategy == SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.FOLLOWING)
                {

                    NotifyServiceHelper.INFO($"通過區域-[{_Region.Name}]可跟車!");
                    this.WaitPointSelectStrategy = WaitPointSelectStrategy;
                    return true;
                }

                int tagOfWaitingForEntryRegion = _SelectTagOfWaitingPoint(_Region, WaitPointSelectStrategy);

                bool NoWaitingPointSetting = tagOfWaitingForEntryRegion == null || tagOfWaitingForEntryRegion == 0;

                MapPoint waitingForEntryPoint = StaMap.GetPointByTagNumber(tagOfWaitingForEntryRegion);

                bool isWaitingAtParkableStation = waitingForEntryPoint.StationType != MapPoint.STATION_TYPE.Normal;

                await CycleStopRequestAsync();
                await Task.Delay(1000);
                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                {
                    await Task.Delay(1000);
                }
                Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion = true;
                NotifyServiceHelper.INFO($"[{Agv.Name}] 即將前往 [{_Region.Name}] 等待點 ({WaitPointSelectStrategy})");
                if (isWaitingAtParkableStation)
                {
                    OrderHandlerFactory orderFactory = new OrderHandlerFactory();
                    var chargeOrderHandler = orderFactory.CreateHandler(new clsTaskDto()
                    {
                        Action = ACTION_TYPE.Park,
                        DesignatedAGVName = this.Agv.Name,
                        TaskName = this.OrderData.TaskName,
                        To_Station = waitingForEntryPoint.TagNumber + ""
                    });

                    while (chargeOrderHandler.SequenceTaskQueue.Count != 0)
                    {
                        var task = chargeOrderHandler.SequenceTaskQueue.Dequeue();
                        Agv.taskDispatchModule.OrderHandler.RunningTask = task;
                        if (task.ActionType == ACTION_TYPE.None)
                            await task.SendTaskToAGV();
                        else
                            await task.DistpatchToAGV();
                    }
                    await Task.Delay(1000);
                    while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                    {
                        await Task.Delay(1000);
                    }
                }
                else
                {
                    MoveTaskDynamicPathPlanV2 _moveToRegionWaitPointsTask = new MoveTaskDynamicPathPlanV2(Agv, new clsTaskDto
                    {
                        Action = ACTION_TYPE.None,
                        To_Station = NoWaitingPointSetting ? finalMapPoint.TagNumber + "" : tagOfWaitingForEntryRegion + "",
                        TaskName = OrderData.TaskName,
                        DesignatedAGVName = Agv.Name,
                    })
                    {
                        Stage = VehicleMovementStage.Traveling_To_Region_Wait_Point
                    };
                    Agv.taskDispatchModule.OrderHandler.RunningTask = _moveToRegionWaitPointsTask;
                    await _moveToRegionWaitPointsTask.SendTaskToAGV();
                }

                await Task.Delay(200);

                Agv.NavigationState.ResetNavigationPoints();
                Agv.NavigationState.ResetNavigationPointsOfPathCalculation();

                await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                Agv.taskDispatchModule.OrderHandler.RunningTask = this;
                Agv.taskDispatchModule.OrderHandler.RunningTask.UpdateMoveStateMessage($"等待可進入 {_Region.Name}..");
                await RegionManager.StartWaitToEntryRegion(Agv, _Region, _TaskCancelTokenSource.Token);
                if (_TaskCancelTokenSource.Token.IsCancellationRequested)
                    throw new TaskCanceledException();

                Agv.NavigationState.RegionControlState.NextToGoRegion = _Region;
                if (isWaitingAtParkableStation)
                {
                    MapPoint entryPtOfChargeStation = Agv.currentMapPoint.TargetNormalPoints().First();
                    DischargeTask dischargeTask = new DischargeTask(Agv, new clsTaskDto
                    {
                        Action = ACTION_TYPE.Discharge,
                        DesignatedAGVName = this.Agv.Name,
                        TaskName = this.OrderData.TaskName,
                        To_Station = entryPtOfChargeStation.TagNumber + ""
                    });

                    Agv.taskDispatchModule.OrderHandler.RunningTask = dischargeTask;
                    await dischargeTask.DistpatchToAGV();
                    await Task.Delay(1000);
                    while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                    {
                        await Task.Delay(1000);
                    }
                }
                Agv.taskDispatchModule.OrderHandler.RunningTask = this;
                IsPathPassMuiltRegions(finalMapPoint, out List<MapRegion> _nextRegions);
                return await RegionPathNavigation(_nextRegions);
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion = false;
                Agv.taskDispatchModule.OrderHandler.RunningTask = this;
            }

            #region local methods


            int _SelectTagOfWaitingPoint(MapRegion region, SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY strategy)
            {
                int waitingTagSetting = region.EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag))
                                                               .OrderBy(pt => pt.CalculateDistance(Agv.currentMapPoint))
                                                               .GetTagCollection()
                                                               .FirstOrDefault();

                List<MapPoint> pointsOfRegion = region.GetPointsInRegion();
                MapPoint neariestPointInRegion = region.GetNearestPointOfRegion(Agv);

                if (strategy == SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.ANY)
                {
                    var tagsOfPtInRegion = pointsOfRegion.GetTagCollection();
                    // 管制區域內的車輛未來不會與當前等待車輛同邊(行徑路線反向)，找到離管制區域最近的點
                    PathFinder pf = new PathFinder();
                    var optimizedPathToRegion = pf.FindShortestPath(StaMap.Map, Agv.currentMapPoint, neariestPointInRegion, new PathFinderOption
                    {
                        Strategy = PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE,
                        OnlyNormalPoint = true,
                    });

                    MapPoint nearestToRegionPoint = optimizedPathToRegion.stations.Take(optimizedPathToRegion.stations.Count - 1)
                                                  .Last(pt => !pt.IsVirtualPoint);
                    if (nearestToRegionPoint == null)
                        return 0;

                    if (neariestPointInRegion.TargetNormalPoints().Any(pt => pt.TagNumber == this.Agv.currentMapPoint.TagNumber))
                        return Agv.currentMapPoint.TagNumber;

                    return nearestToRegionPoint.TagNumber;
                }
                else
                {
                    return waitingTagSetting;
                }
            }
            #endregion
        }

        private bool TryGetWaitingPointSelectStregy(MapRegion region, List<string> inRegionVehiclesNames, out SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY Strategy)
        {
            Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.ANY;
            IEnumerable<IAGV> inRegionVehicles = inRegionVehiclesNames.Select(name => VMSManager.GetAGVByName(name));

            if (region.MaxVehicleCapacity == 1 && inRegionVehicles.Count() < 1)
                return false;

            IAGV inRegionVehicle = inRegionVehicles.FirstOrDefault();
            if (inRegionVehicle == null)
                return false;

            List<MapPoint> pointsOfRegion = region.GetPointsInRegion();

            if (inRegionVehicle.NavigationState.NextNavigtionPoints.Count() == 0)
            {
                Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SELECT_NO_BLOCKED_PATH_POINT;
                return false;
            }
            //{
            //    Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SAME_REGION;
            //    return false;
            //}

            MapPoint nextDestinePointOfInRegionVehicle = inRegionVehicle.NavigationState.NextNavigtionPoints.Last();
            MapRegion nextDestineRegionOfInRegionVehicle = nextDestinePointOfInRegionVehicle.GetRegion();
            MapRegion currentRegion = Agv.currentMapPoint.GetRegion();

            if ((currentRegion.Name == nextDestineRegionOfInRegionVehicle.Name) && currentRegion.Name == region.Name)
            {
                Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SAME_REGION;
                return true;
            }

            bool _NeedGoToPointToYieldPath = _WillInRegionVehicleGoHereSide();

            if (!_NeedGoToPointToYieldPath && nextDestineRegionOfInRegionVehicle.Name != region.Name) //同向而且不會停在管制區內
            {
                Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.FOLLOWING;
                return true;
            }
            Strategy = _NeedGoToPointToYieldPath ? SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SELECT_NO_BLOCKED_PATH_POINT : SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.ANY;
            return true;
            //在管制區域內的車輛，未來是否會與當前等待車輛同邊(行徑路線反向)
            bool _WillInRegionVehicleGoHereSide()
            {
                return currentRegion.Name == nextDestineRegionOfInRegionVehicle.Name || region.Name == nextDestineRegionOfInRegionVehicle.Name;
            }
        }


        /// <summary>
        /// 避車動作
        /// </summary>
        /// <returns></returns>
        /// <exception cref="TaskCanceledException"></exception>
        private async Task AvoidPathProcess(int _seq)
        {
            await SendCancelRequestToAGV();
            _previsousTrajectorySendToAGV.Clear();
            ACTION_TYPE avoidAction = Agv.NavigationState.AvoidActionState.AvoidAction;
            MapPoint parkStation = Agv.NavigationState.AvoidActionState.AvoidPt;
            MapPoint AvoidToPtMoveDestine = Agv.NavigationState.AvoidActionState.AvoidToPtMoveDestine;
            string TaskName = OrderData.TaskName;
            var _avoidToAgv = Agv.NavigationState.AvoidActionState.AvoidToVehicle;
            var runningTaskOfAvoidToVehicle = _avoidToAgv.CurrentRunningTask() as MoveTaskDynamicPathPlanV2;
            if (runningTaskOfAvoidToVehicle == null)
                return;
            UpdateMoveStateMessage($"Before Avoid Path Check...");
            Stopwatch _cancelAvoidTimer = Stopwatch.StartNew();
            //while (avoidAction == ACTION_TYPE.None && _cancelAvoidTimer.Elapsed.TotalSeconds < 2)
            //{
            //    await Task.Delay(1);
            //    if (!_avoidToAgv.NavigationState.IsWaitingConflicSolve && !_avoidToAgv.NavigationState.RegionControlState.IsWaitingForEntryRegion)
            //    {
            //        UpdateMoveStateMessage($"避車動作取消-因避讓車輛已有新路徑");
            //        NotifyServiceHelper.INFO($"{Agv.Name}避車動作取消-因避讓車輛已有新路徑!");
            //        Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
            //        await Task.Delay(500);
            //        return;
            //    }
            //}
            if (!_avoidToAgv.NavigationState.IsWaitingConflicSolve && !_avoidToAgv.NavigationState.RegionControlState.IsWaitingForEntryRegion)
            {
                Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
                return;
            }
            _seq += 1;

            var trafficAvoidTask = new MoveTaskDynamicPathPlanV2(Agv, new clsTaskDto
            {
                Action = ACTION_TYPE.None,
                To_Station = AvoidToPtMoveDestine.TagNumber + "",
                TaskName = TaskName,
                DesignatedAGVName = Agv.Name,
            })
            {
                TaskName = TaskName,
                Stage = avoidAction == ACTION_TYPE.None ? VehicleMovementStage.AvoidPath : VehicleMovementStage.AvoidPath_Park,
            };

            if (this.parentTaskBase == null)
                trafficAvoidTask.parentTaskBase = this; //儲存父任務
            else
                trafficAvoidTask.parentTaskBase = this.parentTaskBase;

            Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
            Agv.taskDispatchModule.OrderHandler.RunningTask = trafficAvoidTask;
            trafficAvoidTask.UpdateMoveStateMessage($"避車中...前往 {AvoidToPtMoveDestine.TagNumber}");
            Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
            await trafficAvoidTask.SendTaskToAGV();
            Agv.NavigationState.State = VehicleNavigationState.NAV_STATE.AVOIDING_PATH;
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            Agv.NavigationState.ResetNavigationPoints();
            //UpdateMoveStateMessage($"Wait {_avoidToAgv.Name} Pass Path...");
            //確認車子已經抵達避車點停止點或貨架停車的二次定位點
            if (avoidAction == ACTION_TYPE.Park && Agv.currentMapPoint.TagNumber == trafficAvoidTask.OrderData.To_Station_Tag)
            {
                try
                {
                    ParkTask parkTask = new ParkTask(Agv, new clsTaskDto()
                    {
                        Action = ACTION_TYPE.Park,
                        TaskName = TaskName,
                        To_Station = parkStation.TagNumber + "",
                        To_Slot = "0",
                        Height = 0,
                        DesignatedAGVName = Agv.Name,
                    })
                    {
                        TaskName = TaskName
                    };
                    await parkTask.DistpatchToAGV();

                    Agv.taskDispatchModule.OrderHandler.RunningTask = parkTask;
                    Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                    await Task.Delay(1000);
                    LeaveParkStationConflicDetection leaveDetector = new LeaveParkStationConflicDetection(AvoidToPtMoveDestine, Agv.states.Coordination.Theta, Agv);
                    var leaveCheckResult = DETECTION_RESULT.NG;
                    while (leaveCheckResult != DETECTION_RESULT.OK)
                    {
                        if (IsTaskCanceled || IsTaskAborted())
                            throw new TaskCanceledException();
                        var detectResult = leaveDetector.Detect();
                        leaveCheckResult = detectResult.Result;
                        if (leaveCheckResult != DETECTION_RESULT.OK)
                            parkTask.UpdateMoveStateMessage($"等待 {AvoidToPtMoveDestine.Graph.Display} 可通行\r\n({detectResult.Message})");
                        await Task.Delay(1000);
                    }

                    int ToStationTagOfDischargeAction = parkTask.TaskDonwloadToAGV.Homing_Trajectory.First().Point_ID;
                    DischargeTask disChargeTask = new DischargeTask(Agv, new clsTaskDto()
                    {
                        Action = ACTION_TYPE.Discharge,
                        TaskName = TaskName,
                        DesignatedAGVName = Agv.Name,
                        To_Station = ToStationTagOfDischargeAction + ""
                    })
                    { TaskName = TaskName };

                    Agv.taskDispatchModule.OrderHandler.RunningTask = disChargeTask;
                    await disChargeTask.DistpatchToAGV();
                    Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                    Agv.taskDispatchModule.OrderHandler.RunningTask = this;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            await Task.Delay(1000);

            //Stopwatch sw = Stopwatch.StartNew();
            //while (avoidAction == ACTION_TYPE.None && _avoidToAgv.main_state == clsEnums.MAIN_STATUS.IDLE)
            //{
            //    if (_avoidToAgv.CurrentRunningTask().IsTaskCanceled)
            //        throw new TaskCanceledException();
            //    //if (sw.Elapsed.TotalSeconds > 5 && (_avoidToAgv.NavigationState.IsWaitingForEntryRegion || _avoidToAgv.NavigationState.IsWaitingConflicSolve || _avoidToAgv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING))
            //    if (sw.Elapsed.TotalSeconds > 5 || (_avoidToAgv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING))
            //        break;
            //    trafficAvoidTask.UpdateMoveStateMessage($"Wait {_avoidToAgv.Name} Start Go..{sw.Elapsed.ToString()}");
            //    await Task.Delay(1000);
            //}
            //sw.Restart();
            Agv.taskDispatchModule.OrderHandler.RunningTask = this.parentTaskBase == null ? this : this.parentTaskBase;

            await WaitToGoalPathIsClear();

            //等待當前點至終點的路徑可通行 : 路徑沒有被其他車輛占用
            async Task WaitToGoalPathIsClear()
            {
                // low level search to destine
                IEnumerable<MapPoint> _GetRegistedPointsToGoal()
                {
                    var _optimizedPath = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, finalMapPoint, null);
                    return _optimizedPath.Where(pt => OtherAGV.Any(agv => agv.NavigationState.NextNavigtionPoints.GetTagCollection().Contains(pt.TagNumber)));
                }
                UpdateMoveStateMessage($"等待有路徑可移動至目的地...");
                while (_GetRegistedPointsToGoal().Any())
                {
                    await Task.Delay(100);
                    if (IsTaskAborted())
                        throw new TaskCanceledException();
                    if (OtherAGV.Any(agv => agv.NavigationState.currentConflicToAGV?.Name == this.Agv.Name))
                    {
                        Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion = true;
                        await CycleStopRequestAsync();
                        break;
                    }
                }
                TrafficWaitingState.SetStatusNoWaiting();
            }
        }

        private bool IsPathPassMuiltRegions(MapPoint finalMapPoint, out List<MapRegion> regions)
        {
            var _optimizedPath = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, finalMapPoint, null);
            regions = _optimizedPath.GetRegions().ToList();
            return regions.Count >= 2;
        }

        private bool IsTaskAborted()
        {
            return (IsTaskCanceled || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
        }

        private async void Agv_OnMapPointChanged(object? sender, int e)
        {
            var currentPt = Agv.NavigationState.NextNavigtionPoints.FirstOrDefault(p => p.TagNumber == e);
            if (currentPt != null)
            {
                Agv.NavigationState.CurrentMapPoint = currentPt;
                List<int> _NavigationTags = Agv.NavigationState.NextNavigtionPoints.GetTagCollection().ToList();

                var ocupyRegionTags = Agv.NavigationState.NextPathOccupyRegions.SelectMany(rect => new int[] { rect.StartPoint.TagNumber, rect.EndPoint.TagNumber })
                                                                                .DistinctBy(tag => tag);

                UpdateMoveStateMessage($"{string.Join("->", ocupyRegionTags)}");

            }
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
    }

}

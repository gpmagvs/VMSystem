﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using MessagePack;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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


        public override void CreateTaskToAGV()
        {
            //base.CreateTaskToAGV();
        }
        public override bool IsAGVReachDestine => Agv?.states?.Last_Visited_Node == DestineTag;



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
        private void HandlePointsChangeToDisabled(object? sender, List<MapPoint> disabledPoints)
        {
            Task.Run(async () =>
            {
                await Task.Delay(1);
                var disabledTags = disabledPoints.GetTagCollection();
                var blockedPointInRemainPath = Agv.NavigationState.NextNavigtionPoints.Where(pt => disabledTags.Contains(pt.TagNumber)).ToList();
                bool IsRemainPathBeDisable = blockedPointInRemainPath.Any();
                if (!IsRemainPathBeDisable)
                    return;
                LogInfoAsync($"Some points({string.Join(",", disabledTags)}) in current navigating path now were disabled. Send Cycle Stop Request To AGV");
                await CycleStopRequestAsync("HandlePointsChangeToDisabled");
            });
        }

        bool CycleStopByWaitingRegionIsEnterable = false;
        bool SecondaryPathSearching = false;
        bool SecondaryPathFound = false;
        private async Task SendTaskToAGV(MapPoint _finalMapPoint)
        {
            LogInfoAsync($"Start SendTaskToAGV .{Agv.Name}-.Destine Goal={_finalMapPoint.TagNumber}");
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            this.parentTaskBase = this;
            Agv.NavigationState.IsWaitingConflicSolve = false;
            cycleStopRequesting = false;
            Agv.NavigationState.IsWaitingForLeaveWorkStationTimeout = false;
            Agv.OnMapPointChanged += Agv_OnMapPointChanged;
            //subStage = Stage;

            bool IsRegionNavigationEnabled = TrafficControlCenter.TrafficControlParameters.Basic.MultiRegionNavigation;
            CancellationTokenSource waitCanPassPtPassableCancle = new CancellationTokenSource();
            MapPoint currentNonPassableByEQPartsReplacing = new MapPoint("", 0);
            void EQFinishPartsReplacedHandler(object sender, int passableTag)
            {
                if (currentNonPassableByEQPartsReplacing == null)
                    return;
                Task.Run(() =>
                {

                    if (passableTag == currentNonPassableByEQPartsReplacing.TagNumber)
                    {
                        NavigationResume(isResumeByWaitTimeout: false);
                        waitCanPassPtPassableCancle.Cancel();
                        LogInfoAsync($"{currentNonPassableByEQPartsReplacing.Graph.Display} now is passable. Resume Navigation");
                    }
                });
            }
            ;


            try
            {
                StaMap.OnPointsDisabled += HandlePointsChangeToDisabled;
                if (_finalMapPoint == null)
                {
                    throw new NoPathForNavigatorException();
                }

                _previsousTrajectorySendToAGV = new List<clsMapPoint>();
                int _seq = 0;
                if (Stage != VehicleMovementStage.AvoidPath && Stage != VehicleMovementStage.AvoidPath_Park && subStage == VehicleMovementStage.Traveling_To_Destine || subStage == VehicleMovementStage.Traveling_To_Source)
                    Agv.NavigationState.StateReset();

                MapPoint searchStartPt = Agv.currentMapPoint.Clone();
                Stopwatch pathConflicStopWatch = new Stopwatch();
                Stopwatch noPathFoundsteadyStopWatch = new();

                pathConflicStopWatch.Start();
                bool isReachNearGoalContinue = false;
                bool isAgvAlreadyAtDestine = Agv.currentMapPoint.TagNumber == finalMapPoint.TagNumber;
                bool isRotationBackMove = false;
                while ((_seq == 0 || _finalMapPoint.TagNumber != Agv.currentMapPoint.TagNumber) || subStage == VehicleMovementStage.AvoidPath || subStage == VehicleMovementStage.AvoidPath_Park || subStage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
                {
                    if ((subStage == VehicleMovementStage.AvoidPath || subStage == VehicleMovementStage.AvoidPath_Park) && Agv.currentMapPoint.TagNumber == _finalMapPoint.TagNumber)
                    {
                        UpdateStateDisplayMessage($"Reach Avoid Point");
                        await WaitAGVNotRunning($"等待停好車在避車點");
                        await AvoidActionProcess();
                        subStage = Stage;
                        await CycleStopRequestAsync();
                        _previsousTrajectorySendToAGV.Clear();
                        _finalMapPoint = this.finalMapPoint;
                        searchStartPt = Agv.currentMapPoint;
                        continue;
                    }


                    if (subStage == VehicleMovementStage.Traveling_To_Region_Wait_Point && Agv.currentMapPoint.TagNumber == _finalMapPoint.TagNumber)
                    {
                        await WaitAGVNotRunning($"等待停好車在區域等待進入點");
                        MapRegion waitingRegion = Agv.NavigationState.RegionControlState.NextToGoRegion;
                        //NotifyServiceHelper.INFO($"[{Agv.Name}] Start Waiting Region-{waitingRegion.Name} Enterable");
                        Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion = true;
                        var agvInWaitingRegion = OtherAGV.Where(agv => agv.currentMapPoint.GetRegion().Name == waitingRegion.Name ||
                                              agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetRegion().Name == waitingRegion.Name));
                        await RegionManager.StartWaitToEntryRegion(Agv, waitingRegion, _TaskCancelTokenSource.Token);
                        subStage = Agv.NavigationState.AvoidActionState.IsAvoidRaising ? subStage : Stage;
                        await CycleStopRequestAsync();
                        _previsousTrajectorySendToAGV.Clear();
                        _finalMapPoint = this.finalMapPoint;
                        searchStartPt = Agv.currentMapPoint;
                        continue;
                    }

                    bool isGoWaitPointByNormalTravaling = false;
                    TaskExecutePauseMRE.WaitOne();
                    if (!Agv.NavigationState.IsWaitingConflicSolve)
                        TrafficWaitingState.SetStatusNoWaiting();
                    await Task.Delay(10);

                    if (DestineChanger != null && DestineChanger.IsCycleStopRaised)
                    {
                        DestineChanger.ReadyToCycleStop();
                        DestineChanger.WaitCycleStopProgressRun();
                    }
                    if (OrderTransfer != null && OrderTransfer.IsCycleStopRaised)
                    {
                        OrderTransfer.ReadyToCycleStop();
                        OrderTransfer.WaitCycleStopProgressRun();
                    }

                    if (IsTaskAborted())
                    {
                        if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                            throw new AGVStatusDownException();
                        else
                            throw new TaskCanceledException();
                    }
                    try
                    {
                        if (Agv.main_state != clsEnums.MAIN_STATUS.RUN)
                        {
                            Agv.NavigationState.ResetNavigationPoints();
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);

                        }

                        Agv.NavigationState.currentConflicToAGV = null;
                        Agv.NavigationState.CurrentConflicRegion = null;
                        Agv.NavigationState.RegionControlState.IsWaitingForEntryRegion = false;

                        if (!OrderData.IsHighestPriorityTask && !Agv.NavigationState.AvoidActionState.IsAvoidRaising && IsPathPassMuiltRegions(Agv.currentMapPoint, _finalMapPoint, out List<MapRegion> regions, out _))
                        {
                            (bool conofirmed, MapRegion nextRegion, MapPoint waitingPoint, isGoWaitPointByNormalTravaling) = await GetNextRegionWaitingPoint(regions);

                            if (conofirmed)
                            {
                                await Task.Delay(400);
                                NotifyServiceHelper.INFO($"[{Agv.Name}] Should Go to Waiting Point({waitingPoint.TagNumber}) of Region-{nextRegion.Name}");
                                await CycleStopRequestAsync("Go to Waiting Point");
                                NotifyServiceHelper.INFO($"[{Agv.Name}] Stop and Ready Go to Waiting Point({waitingPoint.TagNumber}) of Region-{nextRegion.Name}");
                                _previsousTrajectorySendToAGV.Clear();
                                Agv.NavigationState.ResetNavigationPoints();
                                searchStartPt = Agv.currentMapPoint;
                                subStage = VehicleMovementStage.Traveling_To_Region_Wait_Point;
                                if (waitingPoint.StationType != MapPoint.STATION_TYPE.Normal)
                                {
                                    _finalMapPoint = waitingPoint.TargetNormalPoints().First();
                                }
                                else
                                    _finalMapPoint = waitingPoint;

                                Agv.NavigationState.RegionControlState.NextToGoRegion = nextRegion;
                                continue;
                            }
                        }

                        var lastNavigationgoal = Agv.NavigationState.NextNavigtionPoints.LastOrDefault();
                        searchStartPt = lastNavigationgoal == null || Agv.main_state == clsEnums.MAIN_STATUS.IDLE ? Agv.currentMapPoint : lastNavigationgoal;
                        DispatchCenter.GOAL_SELECT_METHOD goalSelectMethod = subStage == VehicleMovementStage.Traveling_To_Region_Wait_Point ? DispatchCenter.GOAL_SELECT_METHOD.TO_GOAL_DIRECTLY :
                                                                                                                                             DispatchCenter.GOAL_SELECT_METHOD.TO_POINT_INFRONT_OF_GOAL;

                        IEnumerable<MapPoint> dispatchCenterReturnPath = null;
                        bool isVehicleNeedParkAtRackAndCurrentPointIsInfrontOfRack = _IsVehicleNeedParkAtRackAndCurrentPointIsInfrontOfRack();
                        if (isVehicleNeedParkAtRackAndCurrentPointIsInfrontOfRack)
                            dispatchCenterReturnPath = new List<MapPoint> { Agv.currentMapPoint };
                        else
                        {
                            bool isNavigationLostTimeout = noPathFoundsteadyStopWatch.ElapsedMilliseconds > 10000;
                            if (isNavigationLostTimeout)
                            {
                                LogInfoAsync($"{Agv.Name}已找不到路徑持續一段時間...{noPathFoundsteadyStopWatch.Elapsed}", true);
                                noPathFoundsteadyStopWatch.Restart();
                            }
                            dispatchCenterReturnPath = (await DispatchCenter.MoveToDestineDispatchRequest(Agv, searchStartPt, _finalMapPoint, OrderData, Stage, goalSelectMethod, isNavigationLostTimeout));
                        }

                        if (subStage == VehicleMovementStage.AvoidPath && !OtherAGV.Any(v => v.NavigationState.currentConflicToAGV == this.Agv))
                        {
                            subStage = Stage;
                            _finalMapPoint = this.finalMapPoint;
                        }

                        if (dispatchCenterReturnPath == null || !dispatchCenterReturnPath.Any())
                        {
                            if (OrderData.isVehicleAssignedChanged && IsTaskCanceled)
                            {
                                throw new TaskCanceledException($"換車且任務已取消");
                            }
                            if (Agv.main_state != clsEnums.MAIN_STATUS.RUN)
                            {
                                Agv.NavigationState.ResetNavigationPoints();
                                await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            }

                            pathConflicStopWatch.Start();
                            searchStartPt = Agv.currentMapPoint;
                            if (string.IsNullOrEmpty(TrafficWaitingState.Descrption))
                                UpdateMoveStateMessage($"Search Path...");
                            await Task.Delay(10);
                            bool _isConflicSolved = false;

                            if (subStage == VehicleMovementStage.Traveling_To_Region_Wait_Point &&
                                !isGoWaitPointByNormalTravaling &&
                                RegionManager.IsRegionEnterable(Agv, Agv.NavigationState.RegionControlState.NextToGoRegion))
                            {
                                //等待衝突的途中，發現區域可進入了
                                await CycleStopRequestAsync("等待衝突的途中，發現區域可進入");
                                subStage = Stage;
                                _finalMapPoint = finalMapPoint;
                                continue;
                            }

                            //if (SecondaryPathSearching == false && Agv.main_state == clsEnums.MAIN_STATUS.IDLE)
                            //{
                            //    SecondaryPathSearching = true;
                            //    bool confilcPathClosed = await DynamicClosePath();
                            //    if (confilcPathClosed)
                            //    {
                            //        SecondaryPathSearching = true;
                            //        await CycleStopRequestAsync();
                            //        _previsousTrajectorySendToAGV.Clear();
                            //        continue;
                            //    }
                            //}

                            if (Agv.main_state == clsEnums.MAIN_STATUS.IDLE && pathConflicStopWatch.Elapsed.TotalSeconds > 3) //無路可走的狀態已經維持超過3秒
                            {
                                Agv.NavigationState.IsWaitingConflicSolve = true;
                                await Task.Delay(1000);
                            }

                            if (Agv.NavigationState.currentConflicToAGV != null && RegionManager.IsAGVWaitingRegion(Agv.NavigationState.currentConflicToAGV, Agv.currentMapPoint.GetRegion()))
                            {
                                //await DynamicClosePath();
                            }

                            if (Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                            {
                                pathConflicStopWatch.Stop();
                                pathConflicStopWatch.Reset();

                                Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
                                Agv.NavigationState.IsWaitingConflicSolve = false;

                                if (Agv.NavigationState.currentConflicToAGV?.main_state == clsEnums.MAIN_STATUS.RUN)
                                {
                                    await Task.Delay(1000);
                                    LogInfoAsync($"{Agv.Name} 避車動作取消因另一車輛已有路徑!");
                                    await CycleStopRequestAsync("避車動作取消因另一車輛已有路徑");
                                    _previsousTrajectorySendToAGV.Clear();
                                    searchStartPt = Agv.currentMapPoint;
                                    continue;
                                }

                                //try get secondary path
                                if (await DynamicClosePath())
                                {
                                    try
                                    {
                                        var _secondaryPathResponse = (await DispatchCenter.MoveToDestineDispatchRequest(Agv, Agv.currentMapPoint, _finalMapPoint, OrderData, Stage));
                                        if (_secondaryPathResponse != null && _secondaryPathResponse.Any())
                                        {
                                            LogInfoAsync($"{Agv.Name} 預估有第二路徑可行走!", true);
                                            await CycleStopRequestAsync("預估有第二路徑可行走");
                                            _previsousTrajectorySendToAGV.Clear();
                                            searchStartPt = Agv.currentMapPoint;
                                            continue;
                                        }
                                        else
                                        {
                                            RestoreClosedPathes();
                                        }
                                    }
                                    catch (NoPathForNavigatorException ex)
                                    {
                                        RestoreClosedPathes();
                                    }
                                }

                                subStage = Agv.NavigationState.AvoidActionState.AvoidAction == ACTION_TYPE.None ? VehicleMovementStage.AvoidPath : VehicleMovementStage.AvoidPath_Park;
                                searchStartPt = Agv.currentMapPoint;

                                LogInfoAsync($"{Agv.Name} 避車動作前Cycle Stop...", true);
                                await CycleStopRequestAsync("避車動作前Cycle Stop");
                                LogInfoAsync($"{Agv.Name} 避車動作開始!", true);
                                _finalMapPoint = subStage == VehicleMovementStage.AvoidPath ? Agv.NavigationState.AvoidActionState.AvoidPt :
                                                                                            Agv.NavigationState.AvoidActionState.AvoidToPtMoveDestine;
                                _previsousTrajectorySendToAGV.Clear();

                                //終點是目前位置，要將_seq設為0好讓迴圈不馬上跳出，才可以轉向正確的角度
                                if (subStage == VehicleMovementStage.AvoidPath_Park && Agv.currentMapPoint.TagNumber == Agv.NavigationState.AvoidActionState.AvoidToPtMoveDestine.TagNumber)
                                    _seq = 0;

                                await Task.Delay(100);
                                continue;
                                //Agv.OnMapPointChanged += Agv_OnMapPointChanged;
                            }
                            if (Agv.NavigationState.SpinAtPointRequest.IsSpinRequesting || Agv.NavigationState.SpinAtPointRequest.IsRaiseByAvoidingVehicleReqest)
                            {
                                await SpinAtCurrentPointProcess(_seq);
                            }
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            //RestoreClosedPathes();
                            continue;
                        }


                        if (IsAnyRotateConflicToOtherVehicle(dispatchCenterReturnPath, out MapRectangle conflicRegion, out bool isConflicAtStartRotation))
                        {
                            LogInfoAsync($"{Agv.Name} 偵測到原路線規劃有旋轉後會與其他車輛發生干涉的區域:{conflicRegion.ToString()}.(是否為起始位置:{(isConflicAtStartRotation ? $"YES,Point : {Agv.currentMapPoint.Graph.Display}" : "NO")})", false);
                            Agv.NavigationState.CurrentConflicRegion = conflicRegion;
                            await DynamicClosePath();
                            isRotationBackMove = isConflicAtStartRotation;
                            continue;
                        }

                        SecondaryPathSearching = false;
                        RestoreClosedPathes();
                        if (subStage != VehicleMovementStage.AvoidPath && subStage != VehicleMovementStage.AvoidPath_Park)
                            Agv.NavigationState.AvoidActionState.Reset();
                        Agv.NavigationState.IsWaitingConflicSolve = false;
                        pathConflicStopWatch.Stop();
                        pathConflicStopWatch.Reset();
                        var nextPath = dispatchCenterReturnPath.ToList();
                        TrafficWaitingState.SetStatusNoWaiting();
                        var nextGoal = nextPath.Last();
                        var remainPath = nextPath.Where(pt => nextPath.IndexOf(nextGoal) >= nextPath.IndexOf(nextGoal));
                        nextPath.First().Direction = int.Parse(Math.Round(Agv.states.Coordination.Theta) + "");

                        try
                        {
                            bool isNextGoalIsAvoidPtDestine = subStage == VehicleMovementStage.AvoidPath && nextPath.LastOrDefault() != null && nextPath.Last().TagNumber == Agv.NavigationState.AvoidActionState.AvoidPt?.TagNumber;
                            if (isNextGoalIsAvoidPtDestine)
                            {
                                double theta = StaMap.GetPointByTagNumber(nextPath.Last().TagNumber).Direction_Avoid;
                                LogInfoAsync($"避車動作且下一終點為避車點，停車角度=避車角度=>{theta}");

                                nextPath.Last().Direction = theta;
                            }
                            else
                            {
                                nextPath.Last().Direction = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.subStage, nextGoal);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogErrorAsync($"嘗試決定終點(Tag={nextPath.Last().TagNumber})之停車角度時發生例外", ex);
                            nextPath.Last().Direction = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.subStage, nextGoal);
                        }


                        var trajectory = PathFinder.GetTrajectory(CurrentMap.Name, nextPath.ToList());
                        bool isGoBackToOriginalPath = IsContainsReversePath(_previsousTrajectorySendToAGV.GetTagList(), trajectory.GetTagList());



                        trajectory = trajectory.Where(pt => !_previsousTrajectorySendToAGV.GetTagList().Contains(pt.Point_ID)).ToArray();

                        if (trajectory.Length == 0)
                        {
                            searchStartPt = Agv.currentMapPoint;
                            continue;
                        }

                        //trajectory.Last().Theta = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, nextGoal);
                        _previsousTrajectorySendToAGV.AddRange(trajectory);

                        if (isGoBackToOriginalPath)
                        {
                            LogInfoAsync($"{Agv.Name} Path revere detected.", true);
                            throw new PathNotDefinedException("Reverse!!!!");
                        }
                        else
                            _previsousTrajectorySendToAGV = _previsousTrajectorySendToAGV.Distinct().ToList();

                        if (!StaMap.RegistPoint(Agv.Name, nextPath, out var msg))
                        {
                            UpdateMoveStateMessage(msg);
                            await CycleStopRequestAsync($"註冊點位失敗({msg})");
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            Agv.NavigationState.ResetNavigationPoints();
                            _previsousTrajectorySendToAGV.Clear();
                            searchStartPt = Agv.currentMapPoint;
                            continue;
                        }

                        Agv.NavigationState.IsWaitingConflicSolve = false;
                        Agv.NavigationState.UpdateNavigationPoints(nextPath);

                        if (IsPathContainPartsReplacingPt(nextPath, out currentNonPassableByEQPartsReplacing))
                        {
                            MapPoint EQPoint = currentNonPassableByEQPartsReplacing.TargetWorkSTationsPoints().FirstOrDefault();
                            bool isChangePathAllowed = false;
                            LogInfoAsync($"{Agv.Name} 開始等待 {EQPoint?.Graph.Display}完成紙捲更換. Tag {currentNonPassableByEQPartsReplacing.TagNumber}尚無法可通行");

                            DispatchCenter.OnPtPassableBecausePartsReplaceFinish += EQFinishPartsReplacedHandler;

                            //如果被封的點位不是終點,設一個等待時間上限並避開該點繞行
                            if (currentNonPassableByEQPartsReplacing.TagNumber != finalMapPoint.TagNumber)
                            {
                                waitCanPassPtPassableCancle = new CancellationTokenSource();
                                int _waitTimeout = TrafficControlCenter.TrafficControlParameters.Navigation.TimeoutWhenWaitPtPassableByEqPartReplacing;
                                LogInfoAsync($"{Agv.Name} 因等待的設備非終點設備， 開始等待倒數 {_waitTimeout}s,若 {EQPoint?.Graph.Display} 紙捲仍未完成更換則進行繞路");
                                TimeSpan ts = TimeSpan.FromSeconds(_waitTimeout);
                                waitCanPassPtPassableCancle.CancelAfter(ts);
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        Stopwatch sw = Stopwatch.StartNew();
                                        while (!waitCanPassPtPassableCancle.IsCancellationRequested)
                                        {
                                            await Task.Delay(1000, waitCanPassPtPassableCancle.Token);
                                            if (!NavigationPausing)
                                                return;
                                            UpdateStateDisplayMessage(PauseNavigationReason + $"\r\n({sw.Elapsed.ToString(@"mm\:ss")}/{ts.ToString(@"mm\:ss")})");
                                        }
                                    }
                                    catch (TaskCanceledException ex)
                                    {

                                    }
                                    finally
                                    {
                                        DispatchCenter.OnPtPassableBecausePartsReplaceFinish -= EQFinishPartsReplacedHandler;

                                        if (NavigationPausing)//取消等待的當下，導航還是暫停=>表示超時等待
                                        {
                                            LogInfoAsync($"{Agv.Name} Wait {currentNonPassableByEQPartsReplacing.TagNumber} 可通行已逾時({_waitTimeout}s),開始繞行!");
                                            Agv.NavigationState.LastWaitingForPassableTimeoutPt = currentNonPassableByEQPartsReplacing;
                                            NavigationResume(isResumeByWaitTimeout: true);
                                            isChangePathAllowed = true;
                                        }
                                        else
                                        {

                                            //因為導航繼續而結束等待
                                        }
                                    }
                                });
                            }
                            string pauseMsg = $"等待設備[{EQPoint?.Graph.Display}]完成紙捲更換...\r\n(Wait [{EQPoint?.Graph.Display}] Paper roller replace finish...)";
                            NavigationPause(isPauseWhenNavigating: false, pauseMsg);
                            UpdateStateDisplayMessage(PauseNavigationReason);
                            await CycleStopRequestAsync($"等待設備[{EQPoint?.Graph.Display}]完成紙捲更換");

                            while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                            {
                                if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                                    throw new AGVStatusDownException();
                                await Task.Delay(100);
                            }
                            _previsousTrajectorySendToAGV.Clear();
                            Agv.NavigationState.ResetNavigationPoints();
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                                throw new AGVStatusDownException();

                            while (IsPathContainPartsReplacingPt(nextPath, out currentNonPassableByEQPartsReplacing))
                            {
                                if (isChangePathAllowed)
                                    break;
                                await DispatchCenter.SyncTrafficStateFromAGVSystemInvoke();
                                await Task.Delay(1000);
                            }
                            NavigationResume(isResumeByWaitTimeout: isChangePathAllowed);

                            continue;
                        }

                        await Task.Delay(100);
                        noPathFoundsteadyStopWatch.Reset();
                        (TaskDownloadRequestResponse responseOfVehicle, clsMapPoint[] _trajectory) = await _DispatchTaskToAGV(new clsTaskDownloadData
                        {
                            Action_Type = ACTION_TYPE.None,
                            Task_Name = OrderData.TaskName,
                            Destination = _finalMapPoint.TagNumber,
                            Trajectory = _previsousTrajectorySendToAGV.ToArray(),
                        });

                        if (responseOfVehicle.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                        {
                            if (responseOfVehicle.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION)
                            {

                            }
                            throw new AGVRejectTaskException(responseOfVehicle.ReturnCode);
                        }
                        if (DestineChanger != null && DestineChanger.IsCycleStopRaised)
                        {
                            DestineChanger.ReStart();
                        }
                        bool isAGVStatusRun = isAgvAlreadyAtDestine || isVehicleNeedParkAtRackAndCurrentPointIsInfrontOfRack ? true : await WaitVehicleStatusRun();

                        _seq += 1;
                        _previsousTrajectorySendToAGV = _trajectory.ToList();
                        MoveTaskEvent = new clsMoveTaskEvent(Agv, nextPath.GetTagCollection(), nextPath.ToList(), false);
                        int nextGoalTag = nextGoal.TagNumber;
                        MapPoint lastGoal = nextGoal;
                        int lastGoalTag = nextGoalTag;
                        try
                        {
                            lastGoal = nextPath[nextPath.Count - 2];
                            lastGoalTag = lastGoal.TagNumber;
                            isReachNearGoalContinue = true;
                        }
                        catch (Exception)
                        {
                            isReachNearGoalContinue = false;
                        }
                        searchStartPt = nextGoal;
                        UpdateMoveStateMessage($"前往-{nextGoal.Graph.Display}");

                        while (nextGoalTag != Agv.currentMapPoint.TagNumber)
                        {
                            if (IsTaskCanceled)
                            {
                                UpdateMoveStateMessage($"任務取消中...");
                                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                                if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                                    throw new AGVStatusDownException();
                                else
                                    throw new TaskCanceledException();
                            }

                            if (lastGoalTag == Agv.currentMapPoint.TagNumber && nextGoalTag != _finalMapPoint.TagNumber)
                            {
                                break;
                            }

                            if (Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                            {
                                if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                                    throw new AGVStatusDownException();
                                else
                                    throw new TaskCanceledException();
                            }
                            if (cycleStopRequesting)
                            {
                                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                                cycleStopRequesting = false;
                                _previsousTrajectorySendToAGV.Clear();
                                searchStartPt = Agv.currentMapPoint;
                                break;
                            }

                            if (subStage == VehicleMovementStage.Traveling_To_Region_Wait_Point &&
                                                                 !isGoWaitPointByNormalTravaling &&
                                                                 RegionManager.IsRegionEnterable(Agv, Agv.NavigationState.RegionControlState.NextToGoRegion))
                            {
                                CycleStopByWaitingRegionIsEnterable = true;
                                await Agv.TaskExecuter.TaskCycleStop(OrderData.TaskName);
                                _previsousTrajectorySendToAGV.Clear();
                                break;
                            }

                            //if (isRotationBackMove)
                            //{
                            //    isRotationBackMove = false;
                            //    await Agv.TaskExecuter.TaskCycleStop(OrderData.TaskName);
                            //    _previsousTrajectorySendToAGV.Clear();
                            //    break;
                            //}

                            await Task.Delay(10);
                        }
                        if (CycleStopByWaitingRegionIsEnterable)
                        {
                            subStage = Stage;
                            _finalMapPoint = finalMapPoint;
                            continue;
                        }
                        _ = Task.Run(async () =>
                        {
                            UpdateMoveStateMessage($"抵達-{nextGoal.Graph.Display}");
                            await Task.Delay(1000);
                        });

                        bool _willRotationFirst(double nextForwardAngle, out double error)
                        {
                            error = Tools.CalculateTheateDiff(Agv.states.Coordination.Theta, nextForwardAngle);
                            return error > 25;
                        }
                    }
                    catch (RotatingOnSpinForbidPtException)
                    {
                        DynamicClosePath();
                        continue;
                    }
                    catch (NoPathForNavigatorException ex)
                    {
                        noPathFoundsteadyStopWatch.Start();
                        UpdateStateDisplayMessage($"No Path Found..Analyzing...");
                        RestoreClosedPathes();
                        await Task.Delay(1000);
                        continue;
                    }
                    catch (PathNotDefinedException ex)
                    {
                        NotifyServiceHelper.ERROR($"[{Agv.Name}] {ex.Message}");
                        LogErrorAsync($"嘗試發送不存在之路徑給{Agv.Name} :{ex.Message},Cycle Stop and Replan...", ex, true);
                        await CycleStopRequestAsync($"PathNotDefinedException");
                        searchStartPt = Agv.currentMapPoint;
                        _previsousTrajectorySendToAGV.Clear();
                        await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                        continue;
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
                        LogErrorAsync(ex.Message, ex);
                        continue;
                    }

                }

                UpdateMoveStateMessage($"抵達-{_finalMapPoint.Graph.Display}-等待停車完成..");



                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN && !IsTaskCanceled)
                {
                    if (IsTaskCanceled)
                        throw new TaskCanceledException();
                    await Task.Delay(100);
                }
                if (IsTaskCanceled)
                    if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                        throw new AGVStatusDownException();
                    else
                        throw new TaskCanceledException();


                await Task.Delay(100);
                double expectedAngle;
                while (!CalculateThetaError(out expectedAngle, out double error))
                {
                    if (IsTaskAborted())
                    {
                        if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN)
                            throw new AGVStatusDownException();
                        else
                            throw new TaskCanceledException();
                    }
                    await FinalStopThetaAdjuctProcess(expectedAngle);
                }
                UpdateMoveStateMessage($"抵達-{_finalMapPoint.Graph.Display}-角度確認({expectedAngle}) OK!");
                await WaitAGVNotRunning($"Reach Destine goal");
                await Task.Delay(100);

            }
            catch (TaskCanceledException ex)
            {
                await WaitAGVNotRunning($"Task Canceled");
                LogErrorAsync($"TaskCanceledException Catch!", ex);
                throw ex;
            }
            catch (AGVRejectTaskException ex)
            {
                LogErrorAsync($"AGVRejectTaskException Catch!", ex);
                throw ex;
            }
            catch (Exception ex)
            {
                LogErrorAsync($"Exception Catch!", ex);
                throw ex;
            }
            finally
            {
                await Task.Delay(500);
                TrafficWaitingState.SetStatusNoWaiting();
                await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
                DispatchCenter.OnPtPassableBecausePartsReplaceFinish -= EQFinishPartsReplacedHandler;
                StaMap.OnPointsDisabled -= HandlePointsChangeToDisabled;
                Agv.NavigationState.StateReset();
                RestoreClosedPathes();
                InvokeTaskDoneEvent();
            }


        }

        /// <summary>
        /// AGV當前需要去RACK PORT避車，且目前的位置已在避車PORT前面且朝向避車PORK
        /// </summary>
        /// <returns></returns>
        private bool _IsVehicleNeedParkAtRackAndCurrentPointIsInfrontOfRack()
        {
            if (subStage != VehicleMovementStage.AvoidPath_Park)
                return false;
            if (Agv.NavigationState.AvoidActionState.AvoidToPtMoveDestine.TagNumber != Agv.currentMapPoint.TagNumber)
                return false;
            //agv need forward to work_station
            double expectThetaForwardTo = Tools.CalculationForwardAngle(Agv.currentMapPoint, Agv.NavigationState.AvoidActionState.AvoidPt);
            return Tools.CalculateTheateDiff(Agv.states.Coordination.Theta, expectThetaForwardTo) < 30;
        }

        private bool IsContainsReversePath(IEnumerable<int> previosPath, IEnumerable<int> newPath)
        {
            if (!previosPath.Any() || !newPath.Any() || previosPath.Count() < 2 || newPath.Count() < 2)
                return false;

            if (previosPath.Last() != newPath.First())
                return false;
            //[1,3]  -> [3,1]

            var intersect = newPath.Skip(1).Reverse().Intersect(previosPath.Take(previosPath.Count() - 1));

            return intersect.Any();
        }

        private async Task<bool> WaitVehicleStatusRun()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            while (Agv.main_state == clsEnums.MAIN_STATUS.IDLE)
            {
                UpdateStateDisplayMessage($"Wait Run...");
                await Task.Delay(10);
                if (cts.IsCancellationRequested)
                    return false;
            }
            return Agv.main_state == clsEnums.MAIN_STATUS.RUN;
        }

        private bool IsPathContainPartsReplacingPt(List<MapPoint> nextPath, out MapPoint noPassablePt)
        {
            var NoPassableTempTags = DispatchCenter.TagListOfInFrontOfPartsReplacingWorkstation
                                                    .ToList();

            noPassablePt = nextPath.FirstOrDefault(pt => NoPassableTempTags.Contains(pt.TagNumber));

            return noPassablePt != null;
        }

        public override async Task SendTaskToAGV()
        {
            finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
            DestineTag = finalMapPoint.TagNumber;
            await SendTaskToAGV(finalMapPoint);
        }

        private bool RestoreClosedPathes()
        {
            if (tempClosePathList.Any())
            {
                foreach (var path in tempClosePathList)
                {
                    StaMap.AddPathDynamic(path);
                }
                tempClosePathList.Clear();
                return true;
            }
            else
                return false;
        }

        private List<MapPath> tempClosePathList = new List<MapPath>();
        private async Task<bool> DynamicClosePath()
        {
            if (Agv.NavigationState.CurrentConflicRegion == null)
            {
                RestoreClosedPathes();
                return false;
            }
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
                    bool _anyRemove = false;
                    foreach (var pt in points)
                    {
                        _anyRemove = await RemovePathAsync(pt, startPtOfPathClose);
                    }
                    return _anyRemove;
                }
                else
                {
                    return false;
                }
            }

            return await RemovePathAsync(startPtOfPathClose, endPtOfPathClose);
            async Task<bool> RemovePathAsync(MapPoint startPt, MapPoint endPt)
            {
                (bool removed, MapPath path) = await StaMap.TryRemovePathDynamic(startPt, endPt);
                if (removed)
                {
                    //NotifyServiceHelper.WARNING($"移除衝突路線-{path.ToString()}");
                    tempClosePathList.Add(path);
                }
                return removed;
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

        private bool CalculateThetaError(out double expectedAngle, out double error)
        {
            expectedAngle = Agv.states.Coordination.Theta;
            if (OrderData.Action != ACTION_TYPE.None)
            {
                int workStationTag = 0;
                if (OrderData.need_change_agv)
                {
                    workStationTag = Stage == VehicleMovementStage.Traveling_To_Source ? OrderData.From_Station_Tag : OrderData.TransferToTag;
                }
                else
                {
                    workStationTag = Stage == VehicleMovementStage.Traveling_To_Source ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;
                }

                MapPoint workStationPoint = StaMap.GetPointByTagNumber(workStationTag);
                MapPoint currentMapPoint = StaMap.GetPointByTagNumber(Agv.currentMapPoint.TagNumber);
                expectedAngle = Tools.CalculationForwardAngle(currentMapPoint, workStationPoint);
            }
            double angleDifference = expectedAngle - Agv.states.Coordination.Theta;
            if (angleDifference > 180)
                angleDifference -= 360;
            else if (angleDifference < -180)
                angleDifference += 360;
            error = Math.Abs(angleDifference);
            UpdateMoveStateMessage($"角度確認({expectedAngle})...");
            return error < 5;
        }
        private async Task FinalStopThetaAdjuctProcess(double expectedAngle)
        {
            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
            clsMapPoint[] trajectory = new clsMapPoint[1] { _previsousTrajectorySendToAGV.Last() };
            trajectory.Last().Theta = expectedAngle;
            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
            await _DispatchTaskToAGV(new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                Destination = finalMapPoint.TagNumber,
                Task_Name = OrderData.TaskName,
                Trajectory = trajectory
            });
            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
        }

        private async Task SpinAtCurrentPointProcess(int _seq)
        {
            double _forwardAngle = Agv.NavigationState.SpinAtPointRequest.ForwardAngle;

            if (CalculateThetaError(_forwardAngle, out _))
                return;


            LogInfoAsync($"{Agv.Name} 原地朝向角度修正任務-朝向角:[{_forwardAngle}] 度");

            _previsousTrajectorySendToAGV.Clear();
            List<MapPoint> _trajPath = new List<MapPoint>() {
                Agv.currentMapPoint.Clone()
            };
            _trajPath.Last().Direction = Agv.NavigationState.SpinAtPointRequest.ForwardAngle;
            clsMapPoint[] traj = PathFinder.GetTrajectory(CurrentMap.Name, _trajPath);
            _seq += 1;
            await CycleStopRequestAsync($"SpinAtCurrentPointProcess");
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
        public MoveTaskDynamicPathPlanV2() : base() { }

        public MoveTaskDynamicPathPlanV2(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public MoveTaskDynamicPathPlanV2(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        private async Task<(bool, MapRegion nextRegion, MapPoint WaitingPoint, bool isGoWaitPointByNormalSegmentTravaling)> GetNextRegionWaitingPoint(List<MapRegion> regions)
        {

            List<MapRegion> regionsFiltered = regions.Where(reg => reg.RegionType != MapRegion.MAP_REGION_TYPE.UNKNOWN && reg.Name != Agv.currentMapPoint.GetRegion()?.Name).ToList();

            MapRegion NextRegion = regionsFiltered.FirstOrDefault(reg => !RegionManager.IsRegionEnterable(Agv, reg));

            if (NextRegion == null)
                return (false, null, null, true);

            TryGetWaitingPointSelectStregy(NextRegion, RegionManager.GetInRegionVehiclesNames(NextRegion), out SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY WaitPointSelectStrategy);
            if (WaitPointSelectStrategy == SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SAME_REGION)
            {
                this.WaitPointSelectStrategy = WaitPointSelectStrategy;
                return (false, null, null, true);
            }

            if (WaitPointSelectStrategy == SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.FOLLOWING)
            {
                //NotifyServiceHelper.INFO($"通過區域-[{NextRegion.Name}]可跟車!");
                return (false, null, null, true);
            }
            int tagOfWaitingForEntryRegion = _SelectTagOfWaitingPoint(NextRegion, WaitPointSelectStrategy);
            MapPoint waitingPoint = StaMap.GetPointByTagNumber(tagOfWaitingForEntryRegion);
            return (true, NextRegion, waitingPoint, false);
        }

        //summery this function 
        /// <summary>
        /// 進入管制區域時，選擇等待點策略
        /// </summary>
        /// <param name="regions"></param>
        /// <returns></returns>
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
        private bool TryGetWaitingPointSelectStregy(MapRegion region, List<string> inRegionVehiclesNames, out SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY Strategy)
        {
            Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.ANY;
            IEnumerable<IAGV> inRegionVehicles = inRegionVehiclesNames.Select(name => VMSManager.GetAGVByName(name));

            if (region.MaxVehicleCapacity == 1 && inRegionVehicles.Count() < 1)
                return false;

            IAGV inRegionVehicle = inRegionVehicles.FirstOrDefault();
            if (inRegionVehicle == null)
                return false;
            int NumberOfIdlingVehicleNotOnMainPath = inRegionVehicles.Count(v => v.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING && v.currentMapPoint.StationType != MapPoint.STATION_TYPE.Normal);

            bool IsUpToLimit = inRegionVehicles.Count() - NumberOfIdlingVehicleNotOnMainPath >= region.MaxVehicleCapacity;
            if (IsUpToLimit)
            {
                Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SELECT_NO_BLOCKED_PATH_POINT;
                return false;
            }
            if (!IsUpToLimit)
            {
                Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.FOLLOWING;
                return true;
            }

            List<MapPoint> pointsOfRegion = region.GetPointsInRegion();

            if (inRegionVehicle.NavigationState.NextNavigtionPoints.Count() == 0)
            {
                Strategy = SELECT_WAIT_POINT_OF_CONTROL_REGION_STRATEGY.SELECT_NO_BLOCKED_PATH_POINT;
                return false;
            }

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

        private bool IsPathPassMuiltRegions(MapPoint startMapPoint, MapPoint finalMapPoint, out List<MapRegion> regions, out MapRegion NextRegion)
        {
            NextRegion = null;
            MapRegion currentRegion = Agv.currentMapPoint.GetRegion();

            try
            {
                var _optimizedPath = LowLevelSearch.GetOptimizedMapPoints(startMapPoint, finalMapPoint, null);
                regions = _optimizedPath.GetRegions().ToList()
                                                     .Where(reg => reg.RegionType != MapRegion.MAP_REGION_TYPE.UNKNOWN && reg.RegionType != MapRegion.MAP_REGION_TYPE.FORBID && reg.Name != "" && reg.Name != currentRegion.Name)
                                                     .ToList();
                if (regions.Any())
                    NextRegion = regions.FirstOrDefault();

                return regions.Count >= 1;
            }
            catch (Exception ex)
            {
                regions = new List<MapRegion>();
                return false;
            }
        }

        private bool IsTaskAborted()
        {
            return (IsTaskCanceled || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING || Agv.main_state == clsEnums.MAIN_STATUS.DOWN);
        }
        private async void Agv_OnMapPointChanged(object? sender, int tagNumber)
        {
            _ = Task.Run(async () =>
              {
                  try
                  {
                      var currentPt = Agv.NavigationState.NextNavigtionPoints.FirstOrDefault(p => p.TagNumber == tagNumber);
                      if (currentPt == null)
                          return;
                      Agv.NavigationState.CurrentMapPoint = currentPt;
                      List<int> _NavigationTags = Agv.NavigationState.NextNavigtionPoints.GetTagCollection().ToList();
                      var ocupyRegionTags = Agv.NavigationState.NextPathOccupyRegions.SelectMany(rect => new int[] { rect.StartPoint.TagNumber, rect.EndPoint.TagNumber })
                                                                                     .DistinctBy(tag => tag);
                      if (!IsTaskCanceled)
                          UpdateMoveStateMessage($"{string.Join("->", ocupyRegionTags)}");
                  }
                  catch (Exception ex)
                  {
                      string _logMsg = $"嘗試更新導航路徑顯示時發生錯誤:{ex.Message}";
                      LogErrorAsync(_logMsg, ex, true);
                      UpdateMoveStateMessage(_logMsg);
                  }
              });
        }

        internal override void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            base.HandleAGVNavigatingFeedback(feedbackData);
        }
    }

}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Drawing;
using VMSystem.Dispatch;
using VMSystem.Dispatch.Regions;
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
    public class MoveTaskDynamicPathPlanV2 : MoveTaskDynamicPathPlan
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

        public bool IsSomeoneWaitingU { get; internal set; } = false;
        public bool IsWaitingSomeone { get; internal set; } = false;
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
            StartRecordTrjectory();
            Agv.NavigationState.IsWaitingConflicSolve = false;
            cycleStopRequesting = false;
            Agv.NavigationState.IsWaitingForLeaveWorkStationTimeout = false;
            Agv.OnMapPointChanged += Agv_OnMapPointChanged;
            bool IsRegionNavigationEnabled = TrafficControlCenter.TrafficControlParameters.Basic.MultiRegionNavigation;
            try
            {
                finalMapPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
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
                        var dispatchCenterReturnPath = (await DispatchCenter.MoveToDestineDispatchRequest(Agv, searchStartPt, OrderData, Stage));
                        //var dispatchCenterReturnPath = (await DispatchCenter.MoveToGoalGetPath(Agv, searchStartPt, OrderData, Stage));

                        if (dispatchCenterReturnPath == null || !dispatchCenterReturnPath.Any() || Agv.NavigationState.AvoidActionState.IsAvoidRaising || IsWaitingSomeone)
                        {
                            if (Stage == VehicleMovementStage.AvoidPath)
                            {
                                Agv.NavigationState.AddCannotReachPointWhenAvoiding(finalMapPoint);
                            }
                            if (IsWaitingSomeone)
                            {
                                IAGV _avoidTo = Agv.NavigationState.AvoidActionState.AvoidToVehicle;
                                Agv.NavigationState.IsWaitingConflicSolve = true;
                                await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                                Agv.NavigationState.ResetNavigationPoints();
                                while (_avoidTo.currentMapPoint.TagNumber != _avoidTo.NavigationState.AvoidActionState.AvoidPt.TagNumber)
                                {
                                    Agv.NavigationState.IsWaitingConflicSolve = true;
                                    UpdateMoveStateMessage($"Wait {_avoidTo.Name} reach {_avoidTo.NavigationState.AvoidActionState.AvoidPt.TagNumber}");
                                    await Task.Delay(1000);
                                }
                                Agv.NavigationState.IsWaitingConflicSolve = false;
                                await SendCancelRequestToAGV();
                                _previsousTrajectorySendToAGV.Clear();
                                searchStartPt = Agv.currentMapPoint;
                                continue;

                            }
                            if (GetRegionChangedToEntryable())
                            {
                                await CycleStopRequestAsync();
                                return;
                            }
                            pathConflicStopWatch.Start();
                            searchStartPt = Agv.currentMapPoint;

                            if (string.IsNullOrEmpty(TrafficWaitingState.Descrption))
                                UpdateMoveStateMessage($"Search Path...");
                            await Task.Delay(10);
                            Agv.NavigationState.ResetNavigationPoints();
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            bool _isConflicSolved = false;

                            if (pathConflicStopWatch.Elapsed.Seconds > 1 && !Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                            {
                                Agv.NavigationState.IsWaitingConflicSolve = true;
                            }

                            if (Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                            {
                                pathConflicStopWatch.Stop();
                                pathConflicStopWatch.Reset();
                                await AvoidPathProcess(_seq);
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
                        Agv.NavigationState.IsWaitingConflicSolve = false;
                        pathConflicStopWatch.Stop();
                        pathConflicStopWatch.Reset();
                        var nextPath = dispatchCenterReturnPath.ToList();
                        TrafficWaitingState.SetStatusNoWaiting();
                        var nextGoal = nextPath.Last();
                        var remainPath = nextPath.Where(pt => nextPath.IndexOf(nextGoal) >= nextPath.IndexOf(nextGoal));
                        nextPath.First().Direction = int.Parse(Math.Round(Agv.states.Coordination.Theta) + "");
                        nextPath.Last().Direction = nextPath.GetStopDirectionAngle(this.OrderData, this.Agv, this.Stage, nextGoal);
                        Agv.NavigationState.UpdateNavigationPoints(nextPath);
                        Agv.NavigationState.IsWaitingConflicSolve = false;
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
                            while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                            {
                                if (IsTaskCanceled)
                                    throw new TaskCanceledException();
                                await Task.Delay(200);
                            }
                            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            Agv.NavigationState.ResetNavigationPoints();
                            _previsousTrajectorySendToAGV.Clear();
                            searchStartPt = Agv.currentMapPoint;
                            continue;
                        }

                        //if (nextPath.Count > 1)
                        //{

                        //    double nextForwardAngle = Tools.CalculationForwardAngle(nextPath.First(), nextPath[1]);
                        //    if (_willRotationFirst(nextForwardAngle, out double error) && Stage == VehicleMovementStage.AvoidPath)
                        //    {
                        //        var spinDetector = new SpinOnPointDetection(this.Agv.currentMapPoint, nextForwardAngle, this.Agv);

                        //        //Agv.NavigationState.AvoidToVehicle.NavigationState.RaiseSpintAtPointRequest(nextForwardAngle, true);
                        //        while (spinDetector.Detect().Result != DETECTION_RESULT.OK && !await WaitSpinDone(Agv.NavigationState.AvoidActionState.AvoidToVehicle, nextForwardAngle))
                        //        {
                        //            Agv.NavigationState.AvoidActionState.AvoidToVehicle.NavigationState.RaiseSpintAtPointRequest(nextForwardAngle, true);
                        //            await Task.Delay(100);
                        //            UpdateMoveStateMessage($"Wait {Agv.NavigationState.AvoidActionState.AvoidToVehicle.Name} Spin to {nextForwardAngle} Degree");
                        //        }
                        //    }
                        //}
                        await _DispatchTaskToAGV(new clsTaskDownloadData
                        {
                            Action_Type = ACTION_TYPE.None,
                            Task_Name = OrderData.TaskName,
                            Destination = Agv.NavigationState.RegionControlState.State == REGION_CONTROL_STATE.WAIT_AGV_REACH_ENTRY_POINT ? nextGoal.TagNumber : DestineTag,
                            Trajectory = _previsousTrajectorySendToAGV.ToArray(),
                            Task_Sequence = _seq
                        });
                        _seq += 1;
                        await Task.Delay(200);
                        MoveTaskEvent = new clsMoveTaskEvent(Agv, nextPath.GetTagCollection(), nextPath.ToList(), false);
                        //UpdateMoveStateMessage($"Go to {nextGoal.TagNumber}");
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
                                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                                {
                                    await Task.Delay(100);
                                    UpdateMoveStateMessage($"任務取消中...");
                                }
                                throw new TaskCanceledException();
                            }

                            if (lastGoalTag == Agv.currentMapPoint.TagNumber && nextGoalTag != finalMapPoint.TagNumber)
                            {
                                break;
                            }


                            if (Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE)
                                throw new TaskCanceledException();
                            if (cycleStopRequesting || IsSomeoneWaitingU)
                            {
                                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                                {
                                    UpdateMoveStateMessage("Cycle Stoping");
                                    await Task.Delay(1000);
                                }
                                cycleStopRequesting = false;
                                _previsousTrajectorySendToAGV.Clear();
                                searchStartPt = Agv.currentMapPoint;
                                break;
                            }
                            if (GetRegionChangedToEntryable())
                            {
                                await CycleStopRequestAsync();
                                return;
                            }
                            await Task.Delay(10);
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

                            //double angleDifference = nextForwardAngle - Agv.states.Coordination.Theta;
                            //if (angleDifference > 180)
                            //    angleDifference -= 360;
                            //else if (angleDifference < -180)
                            //    angleDifference += 360;
                            //error = Math.Abs(angleDifference);
                            //return error > 25;
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
                await Task.Delay(500);
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
                await Task.Delay(500);

            }
            catch (TaskCanceledException ex)
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
                DispatchCenter.CancelDispatchRequest(Agv);
                Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
                Agv.NavigationState.StateReset();
            }


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
        private bool GetRegionChangedToEntryable()
        {
            if (Stage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
            {
                MapRegion nextRegionToGo = Agv.NavigationState.RegionControlState.NextToGoRegion;
                return RegionManager.IsRegionEnterable(Agv, nextRegionToGo, out var inRegionVehicles);
            }
            else
            {
                return false;
            }
        }

        private async Task FinalStopThetaAdjuctProcess()
        {
            await _DispatchTaskToAGV(new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                Destination = finalMapPoint.TagNumber,
                Task_Name = OrderData.TaskName,
                Trajectory = new clsMapPoint[1] { _previsousTrajectorySendToAGV.Last() }
            });
            while (Agv.main_state != clsEnums.MAIN_STATUS.RUN)
            {
                await Task.Delay(1);
                if (IsTaskAborted())
                    throw new TaskCanceledException();
            }
            while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
            {
                await Task.Delay(1);
                if (IsTaskAborted())
                    throw new TaskCanceledException();
            }
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

        private async Task RegionPathNavigation(List<MapRegion> regions)
        {
            if (regions.Count < 2)
                return;

            MapRegion? _Region = regions[1];

            if (!IsNeedToStayAtWaitPoint(_Region, out _))
                return;

            int tagOfWaitingForEntryRegion = _SelectTagOfWaitingPoint(_Region);
            bool NoWaitingPointSetting = tagOfWaitingForEntryRegion == null || tagOfWaitingForEntryRegion == 0;

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
            NotifyServiceHelper.INFO($"{Agv.Name}即將前往 {_Region.Name} 等待點。");
            Agv.NavigationState.RegionControlState.NextToGoRegion = _Region;
            Agv.taskDispatchModule.OrderHandler.RunningTask = _moveToRegionWaitPointsTask;
            await _moveToRegionWaitPointsTask.SendTaskToAGV();
            await Task.Delay(1000);

            Agv.NavigationState.IsWaitingForEntryRegion = true;
            Agv.NavigationState.ResetNavigationPoints();
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            while (IsNeedToStayAtWaitPoint(_Region, out List<string> inRegionVehicles))
            {
                _moveToRegionWaitPointsTask.UpdateMoveStateMessage($"等待 {string.Join(",", inRegionVehicles)} 離開 {_Region.Name}..");
                await Task.Delay(1000);
            }
            Agv.NavigationState.IsWaitingForEntryRegion = false;


            Agv.taskDispatchModule.OrderHandler.RunningTask = this;
            #region local methods

            bool IsNeedToStayAtWaitPoint(MapRegion region, out List<string> inRegionVehicles)
            {
                return !RegionManager.IsRegionEnterable(Agv, region, out inRegionVehicles);
            }
            int _SelectTagOfWaitingPoint(MapRegion region)
            {
                int waitingTagSetting = region.EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag))
                                                               .OrderBy(pt => pt.CalculateDistance(Agv.currentMapPoint))
                                                               .GetTagCollection()
                                                               .FirstOrDefault();
                MapPoint neariestPointInRegion = StaMap.Map.Points.Values.Where(pt => pt.GetRegion(StaMap.Map).Name == region.Name)
                                                                         .OrderBy(pt => pt.CalculateDistance(Agv.states.Coordination))
                                                                         .FirstOrDefault();
                return waitingTagSetting;

            }
            #endregion
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
            bool _isRegionTrafficControl = runningTaskOfAvoidToVehicle.IsWaitingSomeone;
            Stopwatch _cancelAvoidTimer = Stopwatch.StartNew();
            while (avoidAction == ACTION_TYPE.None && !_isRegionTrafficControl && _cancelAvoidTimer.Elapsed.TotalSeconds < 2)
            {
                await Task.Delay(1);
                if (!_avoidToAgv.NavigationState.IsWaitingConflicSolve && !_isRegionTrafficControl && !_avoidToAgv.NavigationState.IsWaitingForEntryRegion)
                {
                    UpdateMoveStateMessage($"避車動作取消-因避讓車輛已有新路徑");
                    NotifyServiceHelper.INFO($"{Agv.Name}避車動作取消-因避讓車輛已有新路徑!");
                    Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
                    await Task.Delay(500);
                    return;
                }
            }
            //SpinOnPointDetection spinDetection = new SpinOnPointDetection(Agv.currentMapPoint, Agv.states.Coordination.Theta - 90, Agv);
            //if ((spinDetection.Detect()).Result == DETECTION_RESULT.OK)
            //{
            //    await SpinAtCurrentPointProcess(_seq);
            //}

            if (!_avoidToAgv.NavigationState.IsWaitingConflicSolve && !_avoidToAgv.NavigationState.IsWaitingForEntryRegion)
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
                Stage = avoidAction == ACTION_TYPE.None ? VehicleMovementStage.AvoidPath : VehicleMovementStage.AvoidPath_Park
            };
            Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
            Agv.taskDispatchModule.OrderHandler.RunningTask = trafficAvoidTask;
            trafficAvoidTask.UpdateMoveStateMessage($"避車中...前往 {AvoidToPtMoveDestine.TagNumber}");
            Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
            await trafficAvoidTask.SendTaskToAGV();
            Agv.NavigationState.State = VehicleNavigationState.NAV_STATE.AVOIDING_PATH;
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            Agv.NavigationState.ResetNavigationPoints();
            //UpdateMoveStateMessage($"Wait {_avoidToAgv.Name} Pass Path...");

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

                    await _WaitReachPointComp(parkStation, parkTask);
                    await Task.Delay(1000);

                    LeaveWorkstationConflicDetection leaveDetector = new LeaveWorkstationConflicDetection(AvoidToPtMoveDestine, Agv.states.Coordination.Theta, Agv);
                    var leaveCheckResult = DETECTION_RESULT.NG;
                    while (leaveCheckResult != DETECTION_RESULT.OK)
                    {
                        var detectResult = leaveDetector.Detect();
                        leaveCheckResult = detectResult.Result;
                        if (leaveCheckResult != DETECTION_RESULT.OK)
                            parkTask.UpdateMoveStateMessage($"等待 {AvoidToPtMoveDestine.Graph.Display} 可通行\r\n({detectResult.Message})");
                        await Task.Delay(1000);

                    }

                    DischargeTask disChargeTask = new DischargeTask(Agv, new clsTaskDto()
                    {
                        Action = ACTION_TYPE.Discharge,
                        TaskName = TaskName,
                        DesignatedAGVName = Agv.Name,
                        To_Station = AvoidToPtMoveDestine.TagNumber + ""
                    })
                    { TaskName = TaskName };

                    Agv.taskDispatchModule.OrderHandler.RunningTask = disChargeTask;
                    await disChargeTask.DistpatchToAGV();

                    await _WaitReachPointComp(AvoidToPtMoveDestine, disChargeTask);

                    Agv.taskDispatchModule.OrderHandler.RunningTask = this;

                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            await Task.Delay(1000);
            Stopwatch sw = Stopwatch.StartNew();
            while (avoidAction == ACTION_TYPE.None && _avoidToAgv.main_state == clsEnums.MAIN_STATUS.IDLE)
            {
                if (_avoidToAgv.CurrentRunningTask().IsTaskCanceled)
                    throw new TaskCanceledException();
                if (sw.Elapsed.TotalSeconds > 5 && (_avoidToAgv.NavigationState.IsWaitingForEntryRegion || _avoidToAgv.NavigationState.IsWaitingConflicSolve || _avoidToAgv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING))
                    break;
                trafficAvoidTask.UpdateMoveStateMessage($"Wait {_avoidToAgv.Name} Start Go..{sw.Elapsed.ToString()}");
                await Task.Delay(1000);
            }
            sw.Restart();
            IsSomeoneWaitingU = false;
            Agv.taskDispatchModule.OrderHandler.RunningTask = this;

            async Task _WaitReachPointComp(MapPoint parkStation, TaskBase task)
            {
                while (Agv.states.Last_Visited_Node != parkStation.TagNumber || Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                {
                    if (task.IsTaskCanceled)
                    {
                        throw new TaskCanceledException();
                    }
                    await Task.Delay(1000);
                    task.UpdateMoveStateMessage($"Wait Park At {parkStation.Graph.Display} Done...");
                }
            }
        }

        private bool IsPathPassMuiltRegions(MapPoint finalMapPoint, out List<MapRegion> regions)
        {
            var _optimizedPath = LowLevelSearch.GetOptimizedMapPoints(Agv.currentMapPoint, finalMapPoint, null);
            regions = _optimizedPath.GetRegions().ToList();
            return regions.Count > 1;
        }

        private bool IsTaskAborted()
        {
            return (IsTaskCanceled || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
        }

        private void Agv_OnMapPointChanged(object? sender, int e)
        {
            var currentPt = Agv.NavigationState.NextNavigtionPoints.FirstOrDefault(p => p.TagNumber == e);
            if (currentPt != null)
            {
                Agv.NavigationState.CurrentMapPoint = currentPt;
                List<int> _NavigationTags = Agv.NavigationState.NextNavigtionPoints.GetTagCollection().ToList();

                var ocupyRegionTags = Agv.NavigationState.NextPathOccupyRegions.SelectMany(rect => new int[] { rect.StartPoint.TagNumber, rect.EndPoint.TagNumber })
                                                         .DistinctBy(tag => tag);

                UpdateMoveStateMessage($"{string.Join("->", ocupyRegionTags)}");
                //UpdateMoveStateMessage($"當前路徑終點:{_NavigationTags.Last()}");
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
            public static IEnumerable<MapPoint> GetOptimizedMapPoints(MapPoint StartPoint, MapPoint GoalPoint, IEnumerable<MapPoint>? constrains, double VehicleCurrentAngle = double.MaxValue)
            {
                PathFinder _pathFinder = new PathFinder();
                clsPathInfo _pathInfo = _pathFinder.FindShortestPath(_Map, StartPoint, GoalPoint, new PathFinderOption
                {
                    OnlyNormalPoint = true,
                    ConstrainTags = constrains == null ? new List<int>() : constrains.GetTagCollection().ToList(),
                    Strategy = PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE,
                    VehicleCurrentAngle = VehicleCurrentAngle
                });

                if (_pathInfo == null || !_pathInfo.stations.Any())
                    throw new NoPathForNavigatorException($"Not any path found from {StartPoint.TagNumber} to {GoalPoint.TagNumber}");

                return _pathInfo?.stations;
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
                if (_OrderAction == ACTION_TYPE.Load || _OrderAction == ACTION_TYPE.Carry) //搬運訂單，要考慮當前是要作取或或是放貨
                {
                    if (stage == VehicleMovementStage.Traveling_To_Destine)
                    {
                        if (orderInfo.need_change_agv)
                            _workStationTag = orderInfo.TransferToTag;
                        else
                            _workStationTag = orderInfo.To_Station_Tag;
                    }
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
                var pt = validPoints.FirstOrDefault();
                if (pt == null)
                {

                }
                return pt;

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
                .Where(pt => StaMap.Map.Points.Values.Any(_pt => _pt.TagNumber == pt.TagNumber))
                .Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal);
        }
        public static IEnumerable<MapPoint> TargetWorkSTationsPoints(this MapPoint mapPoint)
        {
            return mapPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index))
                .Where(pt => StaMap.Map.Points.Values.Any(_p => _p.TagNumber == pt.TagNumber))
                .Where(pt => pt.StationType != MapPoint.STATION_TYPE.Normal);
        }
        public static IEnumerable<MapPoint> TargetParkableStationPoints(this MapPoint mapPoint)
        {
            IEnumerable<MapPoint> stations = mapPoint.TargetWorkSTationsPoints();
            return stations.Where(pt => pt.IsParking);
        }
        public static IEnumerable<MapPoint> TargetParkableStationPoints(this MapPoint mapPoint, ref IAGV AgvToPark)
        {
            IEnumerable<MapPoint> stations = mapPoint.TargetParkableStationPoints();
            //所有被註冊的Tag
            var registedTags = StaMap.RegistDictionary.Keys.ToList();
            List<int> _forbiddenTags = AgvToPark.model == clsEnums.AGV_TYPE.SUBMERGED_SHIELD ? StaMap.Map.TagNoStopOfSubmarineAGV : StaMap.Map.TagNoStopOfForkAGV;
            _forbiddenTags.AddRange(registedTags);
            return stations.Where(pt => pt.IsParking && !_forbiddenTags.Contains(pt.TagNumber));
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
                if (refOrderInfo.Action == ACTION_TYPE.None && stage != VehicleMovementStage.AvoidPath_Park)
                {
                    var fintailStopPt = StaMap.GetPointByTagNumber(finalStopPoint.TagNumber).Clone();
                    if (!nextStopPoint.IsNarrowPath || stage == VehicleMovementStage.AvoidPath || stage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
                    {
                        if (stage == VehicleMovementStage.AvoidPath || stage == VehicleMovementStage.Traveling_To_Region_Wait_Point)
                        {
                            return fintailStopPt.Direction_Avoid;
                        }
                        else
                            return fintailStopPt.Direction;
                    }
                    return _narrowPathDirection(nextStopPoint);
                }
                else
                {
                    MapPoint WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.From_Station_Tag);
                    if (stage == VehicleMovementStage.Traveling_To_Destine)
                    {
                        if (refOrderInfo.need_change_agv)
                            WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.TransferToTag);
                        else
                            WorkStation = StaMap.GetPointByTagNumber(refOrderInfo.To_Station_Tag);
                    }
                    if (stage == VehicleMovementStage.AvoidPath_Park)
                    {
                        WorkStation = executeAGV.NavigationState.AvoidActionState.AvoidPt;
                    }
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
                    tags = StaMap.Map.TagNoStopOfSubmarineAGV;
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
            var conflicAgvs = otherAGVs.Where(agv => pathRegion.Any(segment => segment.IsIntersectionTo(agv.AGVRealTimeGeometery)));

            //get conflic segments 
            var conflicPaths = pathRegion.Where(segment => conflicAgvs.Any(agv => segment.IsIntersectionTo(agv.AGVRealTimeGeometery)));
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
            if (path.Count() <= 1)
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

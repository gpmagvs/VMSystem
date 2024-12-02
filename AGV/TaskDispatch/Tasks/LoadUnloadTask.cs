using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.VMS;
using AGVSystemCommonNet6.Notify;
using VMSystem.Extensions;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public abstract class LoadUnloadTask : TaskBase
    {

        public class ReleaseEntryPtRequest
        {
            public bool Accept { get; set; } = false;
            public MapPoint EntryPoint { get; internal set; } = new MapPoint();
            public IAGV Agv { get; internal set; }

            public string Message { get; internal set; } = string.Empty;
        }

        private MapPoint EntryPoint = new();
        private MapPoint EQPoint = new();
        private ManualResetEvent WaitAGVReachWorkStationMRE = new ManualResetEvent(false);
        private string cargoIDMounted = "";
        public static event EventHandler<ReleaseEntryPtRequest> OnReleaseEntryPointRequesting;

        public override VehicleMovementStage Stage => throw new NotImplementedException();

        public override ACTION_TYPE ActionType => throw new NotImplementedException();

        public override bool IsAGVReachDestine
        {
            get
            {
                return Agv.states.Last_Visited_Node == this.TaskDonwloadToAGV.Homing_Trajectory[0].Point_ID;
            }
        }
        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            throw new NotImplementedException();
        }
        protected abstract void UpdateActionDisplay();
        public override void CreateTaskToAGV()
        {

            base.CreateTaskToAGV();

            EQPoint = StaMap.GetPointByTagNumber(GetDestineWorkStationTagByOrderInfo(OrderData));
            EntryPoint = GetEntryPointsOfWorkStation(EQPoint, Agv.currentMapPoint);
            cargoIDMounted = Agv.states.CSTID.Any() ? Agv.states.CSTID[0] : "";

            this.TaskDonwloadToAGV.Height = GetSlotHeight();
            this.TaskDonwloadToAGV.Destination = EQPoint.TagNumber;
            this.TaskDonwloadToAGV.Homing_Trajectory = new clsMapPoint[2]
            {
                MapPointToTaskPoint(EntryPoint,index:0),
                MapPointToTaskPoint(EQPoint,index:1)
            };
            MoveTaskEvent = new clsMoveTaskEvent(Agv, new List<int> { EntryPoint.TagNumber, EQPoint.TagNumber }, null, false);
        }

        public override async Task SendTaskToAGV()
        {
            bool _isHotRun = OrderData.TaskName.ToLower().Contains("hr_");
            if (!_isHotRun && ActionType == ACTION_TYPE.Unload && Agv.IsAGVHasCargoOrHasCargoID())
            {
                throw new UnloadButAGVHasCargoException();

            }
            if (!_isHotRun && ActionType == ACTION_TYPE.Load && !Agv.IsAGVHasCargoOrHasCargoID())
            {
                throw new LoadButAGVNoCargoException();
            }
            try
            {
                EnterWorkStationDetection enterWorkStationDetection = new(EQPoint, Agv.states.Coordination.Theta, Agv);

                clsConflicDetectResultWrapper detectResult = enterWorkStationDetection.Detect();

                while (detectResult.Result == DETECTION_RESULT.NG)
                {
                    await Task.Delay(200);
                    detectResult = enterWorkStationDetection.Detect();
                    UpdateStateDisplayMessage(detectResult.Message);
                    if (IsTaskCanceled || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING || Agv.CurrentRunningTask().TaskName != this.OrderData.TaskName)
                    {
                        throw new TaskCanceledException();
                    }
                }

                Agv.NavigationState.UpdateNavigationPoints(TaskDonwloadToAGV.Homing_Trajectory.Select(pt => StaMap.GetPointByTagNumber(pt.Point_ID)));
                Agv.NavigationState.LeaveWorkStationHighPriority = Agv.NavigationState.IsWaitingForLeaveWorkStation = false;

                UpdateEQActionMessageDisplay();
                ChangeWorkStationMoveStateBackwarding();
                Agv.OnAGVStatusDown += HandleAGVStatusDown;
                await base.SendTaskToAGV();
                if (AgvStatusDownFlag)
                    return;
                await WaitAGVReachWorkStationTag();
                if (AgvStatusDownFlag)
                    return;
                await WaitAGVTaskDone();
                logger.Info("LUDLD Action End.");
                if (this.ActionType == ACTION_TYPE.Unload)
                    UpdateActualCarrierIDFromAGVStateReported();
                UpdateLDULDTime();
                await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(OrderData.TaskName, EQPoint.TagNumber, ActionType, true, Agv.Name);

            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                //bool isAnyOtherVehicleRunningAndOnMainPath = OtherAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                //                                                     .Where(agv => agv.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal)
                //                                                     .Any();

                try
                {


                    if (TrafficControlCenter.TrafficControlParameters.Experimental.TurnToSpecificThetaWhenLeaveWorkStation &&
                        !AgvStatusDownFlag && !isDestineTagIsSameAsSource() && isAnyOtherVehicleRunningAndOnMainPath())
                    {
                        await TryRotationToAvoidAngle();
                    }


                    bool isDestineTagIsSameAsSource()
                    {
                        if (this.Stage == VehicleMovementStage.WorkingAtDestination)
                            return false;
                        return OrderData.To_Station_Tag == OrderData.From_Station_Tag;
                    }
                    bool isAnyOtherVehicleRunningAndOnMainPath()
                    {
                        return OtherAGV.Where(agv => agv.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal).Any();
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                }
                InvokeTaskDoneEvent();
            }

        }
        static SemaphoreSlim taskTableUseSemaphorse = new SemaphoreSlim(1, 1);

        protected LoadUnloadTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        protected async Task UpdateLDULDTime()
        {

            if (ActionType == ACTION_TYPE.Load || ActionType == ACTION_TYPE.LoadAndPark)
                this.OrderData.LoadTime = DateTime.Now;
            else if (ActionType == ACTION_TYPE.Unload)
                this.OrderData.UnloadTime = DateTime.Now;
            ModifyOrder(this.OrderData);

        }

        protected void UpdateActualCarrierIDFromAGVStateReported()
        {
            this.OrderData.Actual_Carrier_ID = Agv.states.CSTID[0];
            ModifyOrder(OrderData);
        }
        protected override void HandleAGVStatusDown(object? sender, EventArgs e)
        {
            WaitAGVReachWorkStationMRE.Set();
            base.HandleAGVStatusDown(sender, e);
        }


        protected async Task HandleAGVSRejectLDULDActionStartReport(ALARMS alarmCode, string message)
        {
            if (TrafficControlCenter.TrafficControlParameters.Experimental.TurnToAvoidDirectionWhenLDULDActionReject)
                await TryRotationToAvoidAngleOfCurrentTag();
        }

        private async Task WaitAGVReachWorkStationTag()
        {
            WaitAGVReachWorkStationMRE.Reset();
            Agv.TaskExecuter.OnNavigatingReported += TaskExecuter_OnNavigatingReported;
            WaitAGVReachWorkStationMRE.WaitOne();
        }
        private async void TaskExecuter_OnNavigatingReported(object? sender, FeedbackData e)
        {
            if (e.PointIndex == 1 && Agv.currentMapPoint.TagNumber == EQPoint.TagNumber)
            {
                Agv.TaskExecuter.OnNavigatingReported -= TaskExecuter_OnNavigatingReported;
                WaitAGVReachWorkStationMRE.Set();
                string currentNavPath = string.Join("->", Agv.NavigationState.NextNavigtionPoints.GetTagCollection());
                NotifyServiceHelper.INFO($"AGV {Agv.Name} [{ActionType}] 到達工作站- {EQPoint.Graph.Display}({currentNavPath})");
                await Task.Delay(20);
                Agv.NavigationState.ResetNavigationPoints();

                if (TrafficControl.TrafficControlCenter.TrafficControlParameters.Basic.UnLockEntryPointWhenParkAtEquipment) //釋放入口點
                {
                    ReleaseEntryPtRequest request = new ReleaseEntryPtRequest()
                    {
                        Agv = Agv,
                        EntryPoint = EntryPoint,
                        Accept = false
                    };
                    OnReleaseEntryPointRequesting?.Invoke(this, request);
                    if (request.Accept)
                    {
                        (bool confirmed, string errMsg) = await StaMap.UnRegistPoint(Agv.Name, EntryPoint.TagNumber);
                        if (confirmed)
                        {
                            //Notify
                            NotifyServiceHelper.INFO($"AGV {Agv.Name} 解除入口點註冊=> {EntryPoint.Graph.Display}");
                        }
                    }
                    else
                    {
                        NotifyServiceHelper.WARNING($"{Agv.Name} 請求Release Tag {EntryPoint.TagNumber} 已被系統拒絕,原因:{request.Message}");
                        //將進入點的鎖定點也都註冊掉
                        RegistPointOfEntryPointNear();
                    }

                }
                else
                {
                    //將進入點的鎖定點也都註冊掉
                    RegistPointOfEntryPointNear();
                }

            }

            void RegistPointOfEntryPointNear()
            {
                string[] splitedIndexStr = EntryPoint.InvolvePoint.Split(",");
                if (splitedIndexStr.Length > 0)
                {
                    List<int> unregistedInvolvePtTags = EntryPoint.RegistsPointIndexs.Select(_ptIndex => StaMap.GetPointByIndex(_ptIndex))
                                                                                     .Where(pt => !StaMap.RegistDictionary.Keys.Contains(pt.TagNumber))
                                                                                     .Select(unRegistedPt => unRegistedPt.TagNumber)
                                                                                     .ToList();
                    string _tagsStr = string.Join(",", unregistedInvolvePtTags);
                    logger.Info($"{Agv.Name} try regist tags-{unregistedInvolvePtTags} when reach port (Unlock entry point is forbidden case)");
                    bool registedAllSuccess = StaMap.RegistPoint(Agv.Name, unregistedInvolvePtTags, out string msg);
                    if (registedAllSuccess)
                    {
                        string _log = $"Regist {_tagsStr} for {Agv.Name} when entry port (Unlock entry point is forbidden case)";
                        NotifyServiceHelper.INFO(_log);
                        logger.Info(_log);
                    }
                    else
                    {
                        string _log = $"Regist {_tagsStr} for {Agv.Name} when entry port (Unlock entry point is forbidden case) FAIL :{msg}";
                        NotifyServiceHelper.ERROR(_log);
                        logger.Warn(_log);
                    }
                }
            }
        }

        /// <summary>
        /// 轉向避車角度設定
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        private async Task TryRotationToAvoidAngle()
        {
            try
            {
                logger.Trace($"Try make {Agv.Name}  turn to avoid angle.");
                clsMapPoint[] trajectory = this.TaskDonwloadToAGV.ExecutingTrajecory.Take(1).Select(pt => pt).ToArray();
                double avoidTheta = GetTurnToAngleAfterLeaveWorkStation();

                if (Tools.CalculateTheateDiff(avoidTheta, Agv.states.Coordination.Theta) < 10)
                    return;

                trajectory.First().Theta = avoidTheta;
                clsTaskDownloadData taskObj = new clsTaskDownloadData
                {
                    Action_Type = ACTION_TYPE.None,
                    Destination = Agv.currentMapPoint.TagNumber,
                    Task_Name = this.TaskName,
                    Trajectory = trajectory
                };

                if (IsConflicWhenAGVRotating(out List<string> conflicVehicleNames))
                {
                    string vehicleNames = string.Join(",", conflicVehicleNames);
                    logger.Warn($"Spin To Avoid Theta Not Allow. Body Conflic to {vehicleNames}");
                    NotifyServiceHelper.INFO($"預估 [{Agv.Name}] 轉向避車角度與 {vehicleNames} 衝突，禁止旋轉。");
                    return;
                }
                Agv.NavigationState.UpdateNavigationPoints(trajectory.Select(pt => StaMap.GetPointByTagNumber(pt.Point_ID)));
                WaitAGVReachWorkStationMRE.Reset();
                Agv.TaskExecuter.OnActionFinishReported += TaskExecuter_OnActionFinishReported;
                string taskDownloadInfoStr = "Trajectory= " + string.Join("->", taskObj.Trajectory.Select(pt => pt.Point_ID)) + $",Theta={taskObj.Trajectory.Last().Theta}";
                logger.Trace($"Task download info of {Agv.Name} for turn to avoid angle-> {taskDownloadInfoStr}");
                NotifyServiceHelper.WARNING($"{Agv.Name} 即將旋轉至避車角度:{avoidTheta} !");
                (TaskDownloadRequestResponse response, clsMapPoint[] trajectoryReturn) = await Agv.TaskExecuter.TaskDownload(this, taskObj, IsRotateToAvoidAngleTask: true);
                if (response.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
                    logger.Info($"{Agv.Name} turn to avoid angle task download success.");
                else
                {
                    logger.Warn($"{Agv.Name} turn to avoid angle task download  failed.");
                    return;
                }
                WaitAGVReachWorkStationMRE.WaitOne();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                Agv.TaskExecuter.OnActionFinishReported -= TaskExecuter_OnActionFinishReported;
                Agv.NavigationState.ResetNavigationPoints();
            }


        }
        private async Task TryRotationToAvoidAngleOfCurrentTag()
        {
            try
            {

                if (Agv.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.IDLE)
                    return;

                bool isAnyAGVBlockedByThisAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv)
                                                 .Any(agv => agv.NavigationState.IsWaitingConflicSolve && agv.NavigationState.currentConflicToAGV.Name == this.Agv.Name);

                if (ActionType == ACTION_TYPE.Unload && !isAnyAGVBlockedByThisAGV) //如果是放貨，一律要轉向避車角度，因為車上有貨不會再自動產生充電任務
                {
                    logger.Info($"當前沒有任何AGV因與 {Agv.Name}路徑衝突/干涉而正在等待交管，不用轉向避車角度");
                    return;
                }

                logger.Trace($"Try make {Agv.Name} Turn to avoid angle when AGVS Reject action start.");
                int currentTag = Agv.currentMapPoint.TagNumber;
                double avoidTheta = GetTurnToAngleAfterLeaveWorkStation();

                if (Tools.CalculateTheateDiff(avoidTheta, Agv.states.Coordination.Theta) < 10)
                    return;

                clsMapPoint[] traj = PathFinder.GetTrajectory(new List<MapPoint>() { Agv.currentMapPoint });
                traj.First().Theta = avoidTheta;
                clsTaskDownloadData taskObj = new clsTaskDownloadData
                {
                    Action_Type = ACTION_TYPE.None,
                    Destination = Agv.currentMapPoint.TagNumber,
                    Task_Name = this.TaskName,
                    Trajectory = traj
                };

                if (IsConflicWhenAGVRotating(out List<string> conflicVehicleNames))
                {
                    string vehicleNames = string.Join(",", conflicVehicleNames);
                    logger.Warn($"Spin To Avoid Theta Not Allow. Body Conflic to {vehicleNames}");
                    NotifyServiceHelper.INFO($"預估 [{Agv.Name}] 轉向避車角度與 {vehicleNames} 衝突，禁止旋轉。");
                    return;
                }
                Agv.NavigationState.UpdateNavigationPoints(traj.Select(pt => StaMap.GetPointByTagNumber(pt.Point_ID)));
                WaitAGVReachWorkStationMRE.Reset();
                Agv.TaskExecuter.OnActionFinishReported += TaskExecuter_OnActionFinishReported;
                string taskDownloadInfoStr = "Trajectory= " + string.Join("->", taskObj.Trajectory.Select(pt => pt.Point_ID)) + $",Theta={taskObj.Trajectory.Last().Theta}";
                logger.Trace($"Task download info of {Agv.Name} for turn to avoid angle-> {taskDownloadInfoStr}");
                (TaskDownloadRequestResponse response, clsMapPoint[] trajectoryReturn) = await Agv.TaskExecuter.TaskDownload(this, taskObj, IsRotateToAvoidAngleTask: true);
                if (response.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
                    logger.Info($"{Agv.Name} turn to avoid angle task download success.");
                else
                {
                    logger.Warn($"{Agv.Name} turn to avoid angle task download  failed.");
                    return;
                }
                WaitAGVReachWorkStationMRE.WaitOne();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                Agv.TaskExecuter.OnActionFinishReported -= TaskExecuter_OnActionFinishReported;
                Agv.NavigationState.ResetNavigationPoints();
            }
        }

        /// <summary>
        /// 取得離開工作站後的轉向角度
        /// </summary>
        /// <returns></returns>
        private double GetTurnToAngleAfterLeaveWorkStation()
        {
            //TODO : 這裡要改成評估下一個目的地，計算路徑取得航向角度
            MapPoint currentMapPoint = this.EntryPoint;
            MapPoint nextWorkStationMapPoint = null;

            if (Stage == VehicleMovementStage.WorkingAtSource)
                nextWorkStationMapPoint = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
            else if (Stage == VehicleMovementStage.WorkingAtDestination)
            {
                if (TryGetFirstWorkStationOfNextOrder(out MapPoint nextOrderFirstWorkStationMapPoint))
                    nextWorkStationMapPoint = nextOrderFirstWorkStationMapPoint;
            }

            if (nextWorkStationMapPoint != null && _TryGetThetaToTurn(nextWorkStationMapPoint, out double thetaCalculated))
                return thetaCalculated;
            else
                return _defaultTheta();

            bool _TryGetThetaToTurn(MapPoint destineWorkStation, out double _theta)
            {
                _theta = 0;

                PathFinder _pf = new PathFinder();
                PathFinder.PathFinderOption option = new PathFinder.PathFinderOption
                {
                    Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
                    Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
                    OnlyNormalPoint = false
                };

                List<List<MapPoint>> pathesFound = _pf.FindPathes(PathFinder.defaultMap, currentMapPoint, destineWorkStation, option)
                                                      .Where(wrap => wrap.stations.Any())
                                                      .Select(wrap => wrap.stations)
                                                      .Where(path => _isTrunToForwardAngleLessThanThres(path)) //過濾 AGV當前角度旋轉至路徑第一航向角度所需旋轉角度值須小於93度
                                                      .ToList();

                if (!pathesFound.Any())
                    return false;
                var pathPredict = pathesFound.First();
                _theta = Tools.CalculationForwardAngle(pathPredict[0], pathPredict[1]);
                return true;
            }
            bool _isTrunToForwardAngleLessThanThres(List<MapPoint> path)
            {
                double pathFirstForwardAngle = Tools.CalculationForwardAngle(path[0], path[1]);
                double agvCurrnetAngle = this.Agv.states.Coordination.Theta;
                double angleDiff = Tools.CalculateTheateDiff(agvCurrnetAngle, pathFirstForwardAngle);
                return angleDiff < 93;
            }
            double _defaultTheta()
            {
                clsMapPoint[] trajectory = this.TaskDonwloadToAGV.ExecutingTrajecory.Take(1).Select(pt => pt).ToArray();
                int currentTag = trajectory.First().Point_ID;
                return StaMap.GetPointByTagNumber(currentTag).Direction_Avoid;
            };
        }

        private bool IsConflicWhenAGVRotating(out List<string> conficVehiclesNames)
        {
            conficVehiclesNames = new List<string>();
            IAGV thisVehicle = Agv;
            var currentCircleRegion = Agv.currentMapPoint.GetCircleArea(ref thisVehicle);
            var bodyOverlapingVehicles = OtherAGV.Where(_agv => _agv.AGVRealTimeGeometery.IsIntersectionTo(currentCircleRegion)).ToList();
            conficVehiclesNames.AddRange(bodyOverlapingVehicles.Select(v => v.Name));
            var pathConflicToVehicles = OtherAGV.Where(_agv => _agv.NavigationState.NextPathOccupyRegions.Any(reg => reg.IsIntersectionTo(currentCircleRegion))).ToList();
            conficVehiclesNames.AddRange(pathConflicToVehicles.Select(v => v.Name));
            return conficVehiclesNames.Any();
        }

        private bool TryGetFirstWorkStationOfNextOrder(out MapPoint nextOrderFirstWorkStationMapPoint)
        {
            nextOrderFirstWorkStationMapPoint = null;
            //TODO 
            if (!this.Agv.TryGetNextOrder(this.OrderData.TaskName, out clsTaskDto nextOrder))
                return false;
            int nextOrderFirstWorkStationTag = nextOrder.Action == ACTION_TYPE.Carry ? nextOrder.From_Station_Tag : nextOrder.To_Station_Tag;
            nextOrderFirstWorkStationMapPoint = StaMap.GetPointByTagNumber(nextOrderFirstWorkStationTag);
            return nextOrderFirstWorkStationMapPoint != null;
        }

        void TaskExecuter_OnActionFinishReported(object? sender, FeedbackData e)
        {
            Agv.TaskExecuter.OnActionFinishReported -= TaskExecuter_OnActionFinishReported;
            WaitAGVReachWorkStationMRE.Set();
        }


        public override bool IsThisTaskDone(FeedbackData feedbackData)
        {
            if (!base.IsThisTaskDone(feedbackData))
                return false;
            return feedbackData.PointIndex == 0;
        }

        private async Task ChangeWorkStationMoveStateBackwarding()
        {
            await Task.Delay(1500);
            Agv.NavigationState.WorkStationMoveState = VehicleNavigationState.WORKSTATION_MOVE_STATE.FORWARDING;
            await Task.Delay(1500);
            Agv.NavigationState.WorkStationMoveState = VehicleNavigationState.WORKSTATION_MOVE_STATE.BACKWARDING;
        }
        internal async Task UpdateEQActionMessageDisplay()
        {
            ACTION_TYPE orderAction = OrderData.Action;
            string actionString = "";
            string sourceDestineString = "";
            if (orderAction == ACTION_TYPE.Carry)
            {
                MapPoint fromPt = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
                MapPoint toPt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                sourceDestineString = ActionType == ACTION_TYPE.Load ? $"(來源 {(OrderData.IsFromAGV ? Agv.Name : fromPt.Graph.Display)})" : $"(終點 {toPt.Graph.Display})";
            }
            actionString = this.ActionType == ACTION_TYPE.Load ? "放貨" : "取貨";
            await Task.Delay(1000);
            UpdateMoveStateMessage($"{EQPoint.Graph.Display} [{actionString}] 中...\r\n{sourceDestineString}");

        }
        public override void UpdateMoveStateMessage(string msg)
        {
            TrafficWaitingState.SetDisplayMessage($"{msg}");
        }
        protected abstract int GetSlotHeight();
        internal override void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            base.HandleAGVNavigatingFeedback(feedbackData);

            if (feedbackData.LastVisitedNode == DestineTag)
            {
                MoveTaskEvent.AGVRequestState.OptimizedToDestineTrajectoryTagList.Reverse();
            }
        }
        protected virtual int GetDestineWorkStationTagByOrderInfo(clsTaskDto orderInfo)
        {
            if (orderInfo.Action == ACTION_TYPE.Load || orderInfo.Action == ACTION_TYPE.Unload)
            {
                return orderInfo.To_Station_Tag;
            }
            else
            {
                if (this.ActionType == ACTION_TYPE.Unload)
                    return orderInfo.From_Station_Tag;
                else
                    return orderInfo.To_Station_Tag;
            }
        }


        protected bool IsCargoOnAGV()
        {
            return Agv.states.Cargo_Status != 0;
        }


        protected async Task ReportUnloadCargoFromPortDone()
        {
            if (!IsCargoOnAGV())
                return;

            int tag = this.DestineTag;
            int slot = this.GetSlotHeight();
            await AGVSSerivces.CargoUnloadFromPortDoneReport(tag, slot);
        }

        protected async Task ReportLoadCargoToPortDone()
        {
            if (IsCargoOnAGV())
                return;
            int tag = this.DestineTag;
            int slot = this.GetSlotHeight();
            await AGVSSerivces.CargoLoadToPortDoneReport(tag, slot, cargoIDMounted);
        }
    }
}

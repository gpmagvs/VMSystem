using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.VMS;
using AGVSystemCommonNet6.Notify;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public abstract class LoadUnloadTask : TaskBase
    {
        private MapPoint EntryPoint = new();
        private MapPoint EQPoint = new();
        private ManualResetEvent WaitAGVReachWorkStationMRE = new ManualResetEvent(false);


        public LoadUnloadTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

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
                await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(EQPoint.TagNumber, ActionType, Agv.Name);

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

                bool isAnyOtherVehicleRunningAndOnMainPath = OtherAGV.Where(agv => agv.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal)
                                                                     .Any();

                if (!AgvStatusDownFlag && isAnyOtherVehicleRunningAndOnMainPath)
                {
                    await TryRotationToAvoidAngle();
                }
                InvokeTaskDoneEvent();
            }

        }

        protected override void HandleAGVStatusDown(object? sender, EventArgs e)
        {
            WaitAGVReachWorkStationMRE.Set();
            base.HandleAGVStatusDown(sender, e);
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

                await Task.Delay(500);
                Agv.NavigationState.ResetNavigationPoints();
                if (TrafficControl.TrafficControlCenter.TrafficControlParameters.Basic.UnLockEntryPointWhenParkAtEquipment) //釋放入口點
                {
                    (bool confirmed, string errMsg) = await StaMap.UnRegistPoint(Agv.Name, EntryPoint.TagNumber);
                    if (confirmed)
                    {
                        //Notify
                        NotifyServiceHelper.INFO($"AGV {Agv.Name} 解除入口點註冊=> {EntryPoint.Graph.Display}");
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



            void TaskExecuter_OnActionFinishReported(object? sender, FeedbackData e)
            {
                Agv.TaskExecuter.OnActionFinishReported -= TaskExecuter_OnActionFinishReported;
                WaitAGVReachWorkStationMRE.Set();
            }
            try
            {
                logger.Trace($"Try make {Agv.Name}  turn to avoid angle.");


                clsMapPoint[] trajectory = this.TaskDonwloadToAGV.ExecutingTrajecory.Take(1).Select(pt => pt).ToArray();
                int currentTag = trajectory.First().Point_ID;
                double avoidTheta = StaMap.GetPointByTagNumber(currentTag).Direction_Avoid;
                trajectory.First().Theta = avoidTheta;
                clsTaskDownloadData taskObj = new clsTaskDownloadData
                {
                    Action_Type = ACTION_TYPE.None,
                    Destination = Agv.currentMapPoint.TagNumber,
                    Task_Name = this.TaskName,
                    Trajectory = trajectory
                };

                SpinOnPointDetection spinDetection = new SpinOnPointDetection(Agv.currentMapPoint, avoidTheta, Agv);
                clsConflicDetectResultWrapper _resultWarp = spinDetection.Detect();

                if (_resultWarp.Result != DETECTION_RESULT.OK)
                {
                    logger.Info($"Wait Spin To Avoid Theta Allow..\r\n{_resultWarp.Message}");
                    NotifyServiceHelper.INFO($"{Agv.Name} 退出設備後轉向避車角度不允許，如路徑衝突將進入正常避車流程，");
                    return;
                }
                Agv.NavigationState.UpdateNavigationPoints(trajectory.Select(pt => StaMap.GetPointByTagNumber(pt.Point_ID)));
                WaitAGVReachWorkStationMRE.Reset();
                Agv.TaskExecuter.OnActionFinishReported += TaskExecuter_OnActionFinishReported;
                string taskDownloadInfoStr = "Trajectory= " + string.Join("->", taskObj.Trajectory.Select(pt => pt.Point_ID)) + $",Theta={taskObj.Trajectory.Last().Theta}";
                logger.Trace($"Task download info of {Agv.Name} for turn to avoid angle-> {taskDownloadInfoStr}");
                (TaskDownloadRequestResponse response, clsMapPoint[] trajectoryReturn) = await Agv.TaskExecuter.TaskDownload(this, taskObj);
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
    }
}

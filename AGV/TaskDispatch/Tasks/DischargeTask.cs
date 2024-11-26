using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.Dispatch.Regions;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class DischargeTask : TaskBase
    {
        public DischargeTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }
        public DischargeTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override void CreateTaskToAGV()
        {

            MapPoint destinMapPoint = AGVCurrentMapPoint.TargetNormalPoints().FirstOrDefault();
            if (destinMapPoint == null)
            {
                throw new Exception("dicharge No normal station found");
            }
            base.CreateTaskToAGV();
            this.TaskDonwloadToAGV.Destination = destinMapPoint.TagNumber;
            this.TaskDonwloadToAGV.Homing_Trajectory = new clsMapPoint[2]
            {
                MapPointToTaskPoint(AGVCurrentMapPoint,index:0),
                MapPointToTaskPoint(destinMapPoint,index:1)
            };
            MoveTaskEvent = new clsMoveTaskEvent(Agv, new List<int> { AGVCurrentMapPoint.TagNumber, destinMapPoint.TagNumber }, null, false);
        }
        public override async Task SendTaskToAGV()
        {
            try
            {
                //transfer last point of  this.TaskDonwloadToAGV.Homing_Trajectory  to MapPoint and check Station_Type of MapPoint  should be Normal
                if (StaMap.GetPointByTagNumber(this.TaskDonwloadToAGV.Homing_Trajectory.Last().Point_ID).StationType != MapPoint.STATION_TYPE.Normal)
                {
                    throw new Exception("The destination point is not a normal station");
                }

                Agv.NavigationState.LeaveWorkStationHighPriority = Agv.NavigationState.IsWaitingForLeaveWorkStation = false;
                DetermineThetaOfDestine(this.TaskDonwloadToAGV);
                await WaitLeaveWorkStationAllowable(new clsLeaveFromWorkStationConfirmEventArg
                {
                    Agv = this.Agv,
                    GoalTag = TaskDonwloadToAGV.Destination
                });
                MapPoint stationPt = StaMap.GetPointByTagNumber(this.TaskDonwloadToAGV.Homing_Trajectory.Last().Point_ID);

                UpdateMoveStateMessage($"退出-[{stationPt.Graph.Display}]...");
                StaMap.RegistPoint(Agv.Name, TaskDonwloadToAGV.ExecutingTrajecory.GetTagList(), out var msg);
                Agv.OnAGVStatusDown += HandleAGVStatusDown;
                await base.SendTaskToAGV();
                await WaitAGVTaskDone();
            }
            catch (Exception ex)
            {
                logger.Fatal(ex.Message + ex.StackTrace);
                throw ex;
            }
        }
        public override bool IsThisTaskDone(FeedbackData feedbackData)
        {
            if (!base.IsThisTaskDone(feedbackData))
                return false;
            return feedbackData.PointIndex == 1;
        }
        public override async void UpdateMoveStateMessage(string msg)
        {
            await Task.Delay(1000);
            base.UpdateMoveStateMessage(msg);
        }
        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
        }

        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            _taskDownloadData.Homing_Trajectory.Last().Theta = _taskDownloadData.Homing_Trajectory.First().Theta;
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.LeaveFrom_ChargeStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Discharge;


        private async Task WaitLeaveWorkStationAllowable(clsLeaveFromWorkStationConfirmEventArg args)
        {
            try
            {

                //如果退出的二次定位點所在區域不允許兩台車 且訂單任務的終點也在該區域內=>則要在PORT內等待至可通行
                MapPoint secondaryPt = Agv.currentMapPoint.TargetNormalPoints().FirstOrDefault();
                MapRegion regionToReach = secondaryPt.GetRegion();

                MapPoint nextGoal = null;
                if (OrderData.Action == ACTION_TYPE.Carry)
                    nextGoal = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
                else
                    nextGoal = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);

                if (regionToReach.RegionType != MapRegion.MAP_REGION_TYPE.UNKNOWN && _IsNextGoalInRegion(nextGoal, regionToReach))
                {
                    bool isRegionEntryable = false;
                    while (!isRegionEntryable)
                    {
                        isRegionEntryable = RegionManager.IsRegionEnterable(Agv, regionToReach);
                        await Task.Delay(200);
                        UpdateStateDisplayMessage($"暫停在PORT內等待 [{regionToReach.Name}] 區域可以進入..");
                        if (IsTaskCanceled || disposedValue || args.Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                        {
                            throw new TaskCanceledException();
                        }
                    }
                }


                clsLeaveFromWorkStationConfirmEventArg result = new clsLeaveFromWorkStationConfirmEventArg();
                while ((result = await TrafficControlCenter.HandleAgvLeaveFromWorkstationRequest(args)).ActionConfirm != clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK)
                {
                    TrafficWaitingState.SetStatusWaitingConflictPointRelease(new List<int>(), result.Message);
                    await Task.Delay(100);
                    if (IsTaskCanceled || disposedValue || args.Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                    {
                        throw new TaskCanceledException();
                    }
                }
                TrafficWaitingState.SetStatusNoWaiting();
            }
            catch (Exception ex)
            {
                logger.Fatal(ex);
                logger.Error(ex.Message + ex.StackTrace);
                throw ex;
            }

        }

        private bool _IsNextGoalInRegion(MapPoint nextGoal, MapRegion regionToReach)
        {
            bool isGoalNormalPt = nextGoal.StationType == MapPoint.STATION_TYPE.Normal;
            if (isGoalNormalPt)
            {
                return nextGoal.GetRegion().Name == regionToReach.Name;
            }
            else
            {
                return nextGoal.TargetNormalPoints().Any(pt => pt.GetRegion().Name == regionToReach.Name) || nextGoal.GetRegion().Name == regionToReach.Name;
            }
        }

        public override void CancelTask()
        {
            base.CancelTask();
        }
    }
}

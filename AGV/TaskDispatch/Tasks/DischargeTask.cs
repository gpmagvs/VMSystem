using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class DischargeTask : TaskBase
    {
        public DischargeTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
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

                clsLeaveFromWorkStationConfirmEventArg result = new clsLeaveFromWorkStationConfirmEventArg();
                while ((result = await TrafficControlCenter.HandleAgvLeaveFromWorkstationRequest(args)).ActionConfirm != clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK)
                {
                    TrafficWaitingState.SetStatusWaitingConflictPointRelease(new List<int>(), result.Message);
                    await Task.Delay(1000);
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

        public override void CancelTask()
        {
            base.CancelTask();
        }
    }
}

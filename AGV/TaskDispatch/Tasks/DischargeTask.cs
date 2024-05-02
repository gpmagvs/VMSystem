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
            MapPoint destinMapPoint = StaMap.GetPointByIndex(AGVCurrentMapPoint.Target.Keys.First());
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
            DetermineThetaOfDestine(this.TaskDonwloadToAGV);
            await WaitLeaveWorkStationAllowable(new clsLeaveFromWorkStationConfirmEventArg
            {
                Agv = this.Agv,
                GoalTag = TaskDonwloadToAGV.Destination
            });
            await base.SendTaskToAGV();
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
            clsLeaveFromWorkStationConfirmEventArg result = new clsLeaveFromWorkStationConfirmEventArg();
            while ((result = await TrafficControlCenter.HandleAgvLeaveFromWorkstationRequest(args)).ActionConfirm != clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK)
            {
                TrafficWaitingState.SetStatusWaitingConflictPointRelease(new List<int>(), result.Message);
                await Task.Delay(1000);
                if (IsTaskCanceled)
                {
                    break;
                }
            }
            TrafficWaitingState.SetStatusNoWaiting();

        }

        public override void CancelTask()
        {
            base.CancelTask();
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;

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

        }
        public override async Task SendTaskToAGV()
        {
            DetermineThetaOfDestine(this.TaskDonwloadToAGV);
            if (BeforeLeaveFromWorkStation != null)
            {
                clsLeaveFromWorkStationConfirmEventArg TrafficResponse = BeforeLeaveFromWorkStation(new clsLeaveFromWorkStationConfirmEventArg
                {
                    Agv = this.Agv,
                    GoalTag = TaskDonwloadToAGV.Destination
                });

                if (TrafficResponse.ActionConfirm == clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.WAIT)
                {
                    TrafficResponse.WaitSignal.WaitOne();
                }

            }
            base.SendTaskToAGV();
        }
        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
        }

        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            _taskDownloadData.Homing_Trajectory.Last().Theta = _taskDownloadData.Homing_Trajectory.First().Theta;
        }

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.LeaveFrom_ChargeStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Discharge;
    }
}

﻿using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class ChargeTask : TaskBase
    {
        public ChargeTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.WorkingAtChargeStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Charge;

        public override void CreateTaskToAGV()
        {
            base.CreateTaskToAGV();
            MapPoint destinMapPoint = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
            MapPoint sourceMapPoint = StaMap.GetPointByIndex(destinMapPoint.Target.Keys.First());
            base.CreateTaskToAGV();
            this.TaskDonwloadToAGV.Destination = destinMapPoint.TagNumber;
            this.TaskDonwloadToAGV.Homing_Trajectory = new clsMapPoint[2]
            {
                MapPointToTaskPoint(sourceMapPoint,index:0),
                MapPointToTaskPoint(destinMapPoint,index:1)
            };

        }

        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            throw new NotImplementedException();
        }

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }
    }
}

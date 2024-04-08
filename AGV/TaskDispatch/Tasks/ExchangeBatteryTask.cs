using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class ExchangeBatteryTask : ChargeTask
    {

        public ExchangeBatteryTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public override ACTION_TYPE ActionType => ACTION_TYPE.ExchangeBattery;

        public override VehicleMovementStage Stage => VehicleMovementStage.WorkingAtChargeStation;
        public override bool IsAGVReachDestine
        {
            get
            {
                return Agv.states.Last_Visited_Node == this.TaskDonwloadToAGV.Homing_Trajectory[0].Point_ID;
            }
        }
        public override void CreateTaskToAGV()
        {
            base.CreateTaskToAGV();
            MapPoint sourceMapPoint = null;
            MapPoint destinMapPoint = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
            if (destinMapPoint.TagOfInPoint > 0)
            {
                sourceMapPoint = StaMap.GetPointByTagNumber(destinMapPoint.TagOfInPoint);
            }
            else
            {
                sourceMapPoint = StaMap.GetPointByIndex(destinMapPoint.Target.Keys.First());
            }

            this.TaskDonwloadToAGV.InpointOfEnterWorkStation = MapPointToTaskPoint(sourceMapPoint);

            if (destinMapPoint.TagOfOutPoint > 0)
            {
                this.TaskDonwloadToAGV.InpointOfEnterWorkStation = MapPointToTaskPoint(sourceMapPoint);
                this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation = MapPointToTaskPoint(StaMap.GetPointByTagNumber(destinMapPoint.TagOfOutPoint));

            }
            else
            {
                this.TaskDonwloadToAGV.InpointOfEnterWorkStation = MapPointToTaskPoint(sourceMapPoint);
                this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation = MapPointToTaskPoint(sourceMapPoint);

            }

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

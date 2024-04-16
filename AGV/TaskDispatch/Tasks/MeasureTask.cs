using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using System.Xml.Linq;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MeasureTask : TaskBase
    {
        public MeasureTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.MeasureInBay;

        public override ACTION_TYPE ActionType => ACTION_TYPE.Measure;
        public override void CreateTaskToAGV()
        {
            base.CreateTaskToAGV();
            string bayName = OrderData.To_Station;

            if (StaMap.Map.Bays.TryGetValue(bayName, out Bay _bay))
            {
                var _ptList = _bay.Points.ToList();
                _ptList.Insert(0, _bay.InPoint);
                MapPoint outPoint = StaMap.GetPointByName(_bay.OutPoint);
                clsMapPoint[] homingTrajectory = _ptList.Select(name => MapPointToTaskPoint(StaMap.GetPointByName(name))).ToArray();
                this.TaskDonwloadToAGV.Destination = homingTrajectory.First().Point_ID;
                this.TaskDonwloadToAGV.Homing_Trajectory = homingTrajectory;
                this.TaskDonwloadToAGV.InpointOfEnterWorkStation = homingTrajectory[0];
                this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation = MapPointToTaskPoint(outPoint);
            }

        }
        public override bool IsAGVReachDestine
        {
            get
            {
                var agvCurrentTag = Agv.states.Last_Visited_Node;
                var finalTag = this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation.Point_ID;
                var isReach = agvCurrentTag == finalTag;
                LOG.INFO($"Check AGV is reach final goal ,RESULT:{isReach} || AGV at {agvCurrentTag}/Goal:{finalTag}");
                return isReach;
            }
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

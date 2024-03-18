using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using System.Xml.Linq;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MeasureTask : TaskBase
    {
        public MeasureTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public override VehicleMovementStage Stage => VehicleMovementStage.MeasureInBay;

        public override ACTION_TYPE ActionType => ACTION_TYPE.Measure;
        public override void CreateTaskToAGV()
        {
            base.CreateTaskToAGV();
            string bayName = OrderData.To_Station;

            if (StaMap.Map.Bays.TryGetValue(bayName, out Bay _bay))
            {
                var _ptList= _bay.Points.ToList();
                _ptList.Insert(0, _bay.InPoint);
                clsMapPoint[] homingTrajectory = _ptList.Select(name => MapPointToTaskPoint(StaMap.GetPointByName(name))).ToArray();
                this.TaskDonwloadToAGV.Destination = homingTrajectory.First().Point_ID;
                this.TaskDonwloadToAGV.Homing_Trajectory = homingTrajectory;
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

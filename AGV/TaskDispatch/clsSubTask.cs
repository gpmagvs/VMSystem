using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;

namespace VMSystem.AGV.TaskDispatch
{
    public class clsSubTask
    {
        public ACTION_TYPE Action { get; set; }
        public MapPoint Source { get; set; }
        public MapPoint Destination { get; set; }

        /// <summary>
        /// 停車角度
        /// </summary>
        public double DestineStopAngle { get; set; }
        public double StartAngle { get; set; }

        public clsTaskDownloadData DownloadData { get; private set; }
        internal void CreateTaskToAGV(clsTaskDto order, int sequence)
        {
            PathFinder pathFinder = new PathFinder();
            clsMapPoint[] Trajectory = new clsMapPoint[0];
            Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathFinder.FindShortestPath(StaMap.Map.Points, Source, Destination).stations).ToArray();

            DownloadData = new clsTaskDownloadData
            {
                Action_Type = Action,
                Destination = Destination.TagNumber,
                Task_Sequence = sequence,
                Task_Name = order.TaskName,
                Station_Type = Destination.StationType,
            };
            Trajectory.Last().Theta = DestineStopAngle;
            if (Trajectory.Length > 1)
            {
                Trajectory.First().Theta = StartAngle;
            }
            if (Action == ACTION_TYPE.None)
                DownloadData.Trajectory = Trajectory;
            else
            {

                DownloadData.Homing_Trajectory = Trajectory;
            }
        }

    }
}

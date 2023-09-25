using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;

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

        public clsTaskDownloadData DownloadData { get; private set; }
        public string ExecuteOrderAGVName { get; private set; }

        public bool IsSegmentTrejectory => Action == ACTION_TYPE.None && Destination.TagNumber != DownloadData.ExecutingTrajecory.Last().Point_ID;
        internal void CreateTaskToAGV(clsTaskDto order, int sequence)
        {
            ExecuteOrderAGVName = order.DesignatedAGVName;
            PathFinder pathFinder = new PathFinder();
            var optimiedPath = pathFinder.FindShortestPath(StaMap.Map.Points, Source, Destination);
            var TrajectoryToExecute = PathFinder.GetTrajectory(StaMap.Map.Name, optimiedPath.stations).ToArray();
            DownloadData = new clsTaskDownloadData
            {
                Action_Type = Action,
                Destination = Destination.TagNumber,
                Task_Sequence = sequence,
                Task_Name = order.TaskName,
                Station_Type = Destination.StationType,

            };
            TrajectoryToExecute.Last().Theta = DestineStopAngle;
            if (Action == ACTION_TYPE.None)
                DownloadData.Trajectory = TrajectoryToExecute;
            else
            {
                DownloadData.Homing_Trajectory = TrajectoryToExecute;
            }
        }
    }
}

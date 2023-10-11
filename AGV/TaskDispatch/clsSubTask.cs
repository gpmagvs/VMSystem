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
        public string CarrierID { get; set; } = "";
        public List<MapPoint> EntirePathPlan { get; private set; } = new List<MapPoint>();
        public bool IsSegmentTrejectory => Action == ACTION_TYPE.None && Destination.TagNumber != DownloadData.ExecutingTrajecory.Last().Point_ID;
        internal void CreateTaskToAGV(clsTaskDto order, int sequence, bool isMovingSeqmentTask = false, int agv_tag = -1, double agv_angle = -1)
        {
            ExecuteOrderAGVName = order.DesignatedAGVName;
            PathFinder pathFinder = new PathFinder();
            var optimiedPath = pathFinder.FindShortestPath(StaMap.Map.Points, Source, Destination);
            EntirePathPlan = optimiedPath.stations;
            if (Action == ACTION_TYPE.None)
            {
                if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts)
                {
                    
                    Dictionary<string, string> RegistAreaFromPartsDB = TrafficControl.PartsAGVSHelper.QueryAGVSRegistedAreaName();
                    var RegistedPointsByParts = RegistAreaFromPartsDB.Values.Where(item => item != "AMCAGC");
                    var RegistPathToParts = optimiedPath.stations.Where(eachpoint => string.IsNullOrEmpty(eachpoint.Name)).Select(eachpoint => eachpoint.Name).ToList();
                    bool IsRegistSuccess = TrafficControl.PartsAGVSHelper.RegistStationRequestToAGVS(RegistPathToParts, "AMCAGV");
                    //執行註冊的動作 建TCP Socket、送Regist指令、確認可以註冊之後再往下進行，也許可以依照回傳值決定要走到哪裡，這個再跟Parts討論
                }

                //若路徑上有點位被註冊=>移動至被註冊點之前一點
                List<MapPoint> regitedPoints = StaMap.GetRegistedPointsOfPath(optimiedPath.stations, ExecuteOrderAGVName);
                regitedPoints.AddRange(VMSManager.AllAGV.FindAll(agv => agv.Name != ExecuteOrderAGVName).Select(agv => agv.currentMapPoint));
                regitedPoints = regitedPoints.Distinct().ToList();
                if (regitedPoints.Any())
                {
                    var index_of_registed_pt = optimiedPath.stations.FindIndex(pt => pt.TagNumber == regitedPoints.First().TagNumber);
                    if (index_of_registed_pt != -1)
                    {
                        var waitPoint = optimiedPath.stations.Last(pt => !pt.IsVirtualPoint && pt.TagNumber != regitedPoints.First().TagNumber && optimiedPath.stations.IndexOf(pt) < index_of_registed_pt);
                        var index_of_wait_point = optimiedPath.stations.IndexOf(waitPoint);
                        //
                        MapPoint[] seqmentPath = new MapPoint[index_of_wait_point + 1];
                        optimiedPath.stations.CopyTo(0, seqmentPath, 0, seqmentPath.Length);
                        optimiedPath.stations = seqmentPath.ToList();
                    }
                }
            }

            var TrajectoryToExecute = PathFinder.GetTrajectory(StaMap.Map.Name, optimiedPath.stations).ToArray();
            if (!StaMap.RegistPoint(ExecuteOrderAGVName, optimiedPath.stations, out string msg))
            {
                var tags = string.Join(",", optimiedPath.stations.Select(pt => pt.TagNumber));
                throw new Exception($"{ExecuteOrderAGVName}- Regit Point({tags}) Fail...");
            }
            DownloadData = new clsTaskDownloadData
            {
                Action_Type = Action,
                Destination = Destination.TagNumber,
                Task_Sequence = sequence,
                Task_Name = order.TaskName,
                Station_Type = Destination.StationType,
                CST = new clsCST[1] { new clsCST { CST_ID = CarrierID } }

            };
            TrajectoryToExecute.Last().Theta = DestineStopAngle;
            if (isMovingSeqmentTask)
            {
                TrajectoryToExecute.First(pt => pt.Point_ID == agv_tag).Theta = agv_angle;
            }
            if (Action == ACTION_TYPE.None)
                DownloadData.Trajectory = TrajectoryToExecute;
            else
            {
                DownloadData.Homing_Trajectory = TrajectoryToExecute;
            }
        }

        internal MapPoint GetNextPointToGo(MapPoint currentMapPoint)
        {
            try
            {
                return EntirePathPlan.First(pt => pt.TagNumber != currentMapPoint.TagNumber && !pt.IsVirtualPoint && EntirePathPlan.IndexOf(pt) > EntirePathPlan.IndexOf(currentMapPoint));
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}

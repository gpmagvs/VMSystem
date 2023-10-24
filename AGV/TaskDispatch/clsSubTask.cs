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
        internal void CreateTaskToAGV(clsTaskDto order, int sequence, out bool isSegment ,out clsMapPoint lastPt, bool isMovingSeqmentTask = false, int agv_tag = -1, double agv_angle = -1)
        {
            lastPt = null;
            isSegment = false;
            ExecuteOrderAGVName = order.DesignatedAGVName;
            PathFinder pathFinder = new PathFinder();
            var optimiedPath = pathFinder.FindShortestPath(StaMap.Map.Points, Source, Destination);
            EntirePathPlan = optimiedPath.stations;
            if (Action == ACTION_TYPE.None)
            {

                //若路徑上有點位被註冊=>移動至被註冊點之前一點
                List<MapPoint> regitedPoints = StaMap.GetRegistedPointsOfPath(optimiedPath.stations, ExecuteOrderAGVName);

                if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts)
                {//查詢現在Parts用掉的點位
                    Dictionary<string, string> RegistAreaFromPartsDB = TrafficControl.PartsAGVSHelper.QueryAGVSRegistedAreaName();
                    var RegistedPointsByParts = RegistAreaFromPartsDB.Where(item => item.Value != "AMCAGV").Select(item=>item.Key);
                    var PartsRegistedPoints = optimiedPath.stations.Where(item => RegistedPointsByParts.Contains(item.Name));
                    regitedPoints.AddRange(PartsRegistedPoints);
                    foreach (var item in PartsRegistedPoints)
                    {
                        var RegistPointInfo = new clsMapPoiintRegist();
                        item.TryRegistPoint("Parts", out RegistPointInfo);
                    }
                    Task.Run(() => UpdatePartRegistedPointInfo(PartsRegistedPoints.ToList()));
                    
                }

                regitedPoints.AddRange(VMSManager.AllAGV.FindAll(agv => agv.Name != ExecuteOrderAGVName).Select(agv => agv.currentMapPoint));
                regitedPoints = regitedPoints.Distinct().ToList();
                if (regitedPoints.Any()) //有點位被註冊
                {
                    var index_of_registed_pt = optimiedPath.stations.FindIndex(pt => pt.TagNumber == regitedPoints.First().TagNumber);
                    if (index_of_registed_pt != -1)
                    {
                        var waitPoint = optimiedPath.stations.Last(pt => !pt.IsVirtualPoint && pt.TagNumber != regitedPoints.First().TagNumber && optimiedPath.stations.IndexOf(pt) < index_of_registed_pt); //可以走的點就是已經被註冊的點之前的其他點
                        var index_of_wait_point = optimiedPath.stations.IndexOf(waitPoint);
                        //
                        MapPoint[] seqmentPath = new MapPoint[index_of_wait_point + 1];
                        optimiedPath.stations.CopyTo(0, seqmentPath, 0, seqmentPath.Length);
                        optimiedPath.stations = seqmentPath.ToList();
                        isSegment = true;
                        
                    }
                }
                if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts)
                {
                    var RegistPathToParts = optimiedPath.stations.Where(eachpoint => !string.IsNullOrEmpty(eachpoint.Name)).Select(eachpoint => eachpoint.Name).ToList();
                    bool IsRegistSuccess = TrafficControl.PartsAGVSHelper.RegistStationRequestToAGVS(RegistPathToParts, "AMCAGV");
                    //執行註冊的動作 建TCP Socket、送Regist指令、確認可以註冊之後再往下進行，也許可以依照回傳值決定要走到哪裡，這個再跟Parts討論
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
            lastPt = TrajectoryToExecute.Last();
        }

        internal void UpdatePartRegistedPointInfo(List<MapPoint> RegistPointByPart)
        {
            while (true)
            {
                Thread.Sleep(5000);
                Dictionary<string, string> RegistAreaFromPartsDB = TrafficControl.PartsAGVSHelper.QueryAGVSRegistedAreaName();
                var UpdatedRegistedPointsByParts = RegistAreaFromPartsDB.Where(item => item.Value != "AMCAGV").Select(item => item.Key);

                foreach (var item in RegistPointByPart.ToArray())
                {
                    if (!UpdatedRegistedPointsByParts.Contains(item.Name))
                    {
                        string Error;
                        item.TryUnRegistPoint("Parts", out Error);
                        RegistPointByPart.Remove(item);
                    }
                }
                if (RegistPointByPart.Count == 0 )
                {
                    break;
                }

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
        internal MapPoint GetNextPointToGo(clsMapPoint currentMapPoint)
        {
            try
            {
                var tagNumberList = EntirePathPlan.Select(pt => pt.TagNumber).ToList();
                return EntirePathPlan.First(pt => pt.TagNumber != currentMapPoint.Point_ID && !pt.IsVirtualPoint && EntirePathPlan.IndexOf(pt) > tagNumberList.IndexOf(currentMapPoint.Point_ID));
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}

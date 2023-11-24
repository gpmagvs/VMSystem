using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
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

        public MapPoint LastStopPoint { get; set; }

        public clsTaskDownloadData DownloadData { get; private set; }
        public string ExecuteOrderAGVName { get; private set; }
        public string CarrierID { get; set; } = "";
        public List<MapPoint> EntirePathPlan { get; private set; } = new List<MapPoint>();
        public List<MapPoint> SubPathPlan { get; private set; } = new List<MapPoint>();

        public static event EventHandler<List<MapPoint>> OnPathClosedByAGVImpactDetecting;

        public bool IsSegmentTrejectory => Action == ACTION_TYPE.None && Destination.TagNumber != DownloadData.ExecutingTrajecory.Last().Point_ID;
        internal void GenOptimizePathOfTask(clsTaskDto order, int sequence, out bool isSegment, out clsMapPoint lastPt, bool isMovingSeqmentTask = false, int agv_tag = -1, double agv_angle = -1)
        {
            lastPt = null;
            isSegment = false;
            ExecuteOrderAGVName = order.DesignatedAGVName;
            var TargetAGVItem = VMSManager.AllAGV.First(item => item.Name == ExecuteOrderAGVName);

            PathFinder pathFinder = new PathFinder();
            var optimiedPath = pathFinder.FindShortestPath(StaMap.Map, Source, Destination);
            EntirePathPlan = optimiedPath.stations;
            var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv.Name != ExecuteOrderAGVName);
            List<IAGV> agv_too_near_from_path = new List<IAGV>();
            if (Action == ACTION_TYPE.None)
            {
               
                //若路徑上有點位被註冊=>移動至被註冊點之前一點
                List<MapPoint> regitedPoints = StaMap.GetRegistedPointsOfPath(optimiedPath.stations, ExecuteOrderAGVName);

                int NowPositionIndex = LastStopPoint == null ? 0 : optimiedPath.stations.IndexOf(LastStopPoint);
                var FollowingStations = new MapPoint[optimiedPath.stations.Count - NowPositionIndex];
                if (NowPositionIndex == -1)
                {
                    FollowingStations = optimiedPath.stations.ToArray();
                }
                else
                {
                    Array.Copy(optimiedPath.stations.ToArray(), NowPositionIndex, FollowingStations, 0, optimiedPath.stations.Count - NowPositionIndex);
                }
                if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts)
                {
                    //查詢現在Parts用掉的點位
                    Dictionary<string, string> RegistAreaFromPartsDB = TrafficControl.PartsAGVSHelper.QueryAGVSRegistedAreaName().Result;
                    var RegistedPointsByParts = RegistAreaFromPartsDB.Where(item => item.Value != "AMCAGV").Select(item => item.Key);
                    var PartsRegistedPoints = FollowingStations.Where(item => RegistedPointsByParts.Contains(item.Name));
                    regitedPoints.AddRange(PartsRegistedPoints);
                    foreach (var item in PartsRegistedPoints)
                    {
                        var RegistPointInfo = new clsMapPoiintRegist();
                        item.TryRegistPoint("Parts", out RegistPointInfo);
                    }
                    Task.Run(() => UpdatePartRegistedPointInfo(PartsRegistedPoints.ToList()));
                }

                regitedPoints.AddRange(otherAGVList.Select(agv => agv.currentMapPoint));
                regitedPoints = regitedPoints.Where(pt => pt != null).Distinct().ToList();
                if (regitedPoints.Any()) //有點位被註冊
                {
                    var index_of_registed_pt = optimiedPath.stations.FindIndex(pt => pt.TagNumber == regitedPoints.First().TagNumber);
                    if (index_of_registed_pt != -1)
                    {
                        var waitPoint = optimiedPath.stations.LastOrDefault(pt => !pt.IsVirtualPoint && pt.TagNumber != regitedPoints.First().TagNumber && optimiedPath.stations.IndexOf(pt) < index_of_registed_pt); //可以走的點就是已經被註冊的點之前的其他點
                        var index_of_wait_point = optimiedPath.stations.IndexOf(waitPoint);
                        //
                        MapPoint[] seqmentPath = new MapPoint[index_of_wait_point + 1];
                        optimiedPath.stations.CopyTo(0, seqmentPath, 0, seqmentPath.Length);
                        optimiedPath.stations = seqmentPath.ToList();
                        isSegment = true;

                    }
                }
                SubPathPlan = optimiedPath.stations;
                if (TrafficControl.PartsAGVSHelper.NeedRegistRequestToParts)
                {
                    Task.Factory.StartNew(async () =>
                    {
                        var FollwingStationsToRegist = SubPathPlan.Intersect(FollowingStations);
                        var RegistPathToParts = FollwingStationsToRegist.Where(eachpoint => !string.IsNullOrEmpty(eachpoint.Name)).Select(eachpoint => eachpoint.Name).ToList();
                        bool IsRegistSuccess = await TrafficControl.PartsAGVSHelper.RegistStationRequestToAGVS(RegistPathToParts, "AMCAGV");
                    });
                    //執行註冊的動作 建TCP Socket、送Regist指令、確認可以註冊之後再往下進行，也許可以依照回傳值決定要走到哪裡，這個再跟Parts討論
                }
            }
            LastStopPoint = optimiedPath.stations.Last();
            var TrajectoryToExecute = PathFinder.GetTrajectory(StaMap.Map.Name, optimiedPath.stations).ToArray();

            var RegistPath = pathFinder.FindShortestPath(StaMap.Map, TargetAGVItem.currentMapPoint, LastStopPoint);
            agv_too_near_from_path = otherAGVList.Where(_agv => RegistPath.stations.Any(pt => pt.CalculateDistance(_agv.states.Coordination.X, _agv.states.Coordination.Y) * 100.0 <= _agv.options.VehicleLength)).ToList();
            if (agv_too_near_from_path.Any()) //找出路徑上所有干涉點位
            {
                foreach (var agv_too_near in agv_too_near_from_path)
                {
                    var too_near_points = RegistPath.stations.FindAll(pt => pt.CalculateDistance(agv_too_near.currentMapPoint) * 100.0 <= agv_too_near.options.VehicleLength);
                    foreach (var point in too_near_points)
                    {
                        StaMap.RegistPoint(agv_too_near.Name, point, out string errMsg);
                    }
                }
            }

            if (!StaMap.RegistPoint(ExecuteOrderAGVName, RegistPath.stations, out string msg))
            {
                var tags = string.Join(",", optimiedPath.stations.Select(pt => pt.TagNumber));
                throw new Exception($"{ExecuteOrderAGVName}- Regit Point({tags}) Fail...");
            }

            Int32.TryParse(order.From_Station, out var fromTag);
            Int32.TryParse(order.To_Station, out var toTag);
            DownloadData = new clsTaskDownloadData
            {
                Action_Type = Action,
                Destination = Destination.TagNumber,
                Task_Sequence = sequence,
                Task_Name = order.TaskName,
                Station_Type = Destination.StationType,
                CST = new clsCST[1] { new clsCST { CST_ID = CarrierID } },
                OrderInfo = new clsTaskDownloadData.clsOrderInfo
                {
                    ActionName = order.Action,
                    SourceName = StaMap.GetStationNameByTag(fromTag),
                    DestineName = StaMap.GetStationNameByTag(toTag),
                }

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
            var _lastPtTag = lastPt.Point_ID;
            RegistConflicPoints(_lastPtTag, agv_too_near_from_path);
        }

        private void RegistConflicPoints(int _lastPtTag, List<IAGV> agv_too_near_from_path)
        {
            var points = EntirePathPlan.Where(pt => pt.TagNumber != _lastPtTag).ToList();
            if (points.Count() == 0)
                return;
            bool NotAGVConflic = agv_too_near_from_path.Count == 0;
            if (!NotAGVConflic)
                foreach (var item in points)
                {
                    StaMap.RegistPoint(agv_too_near_from_path.First().Name, item, out var msg);
                }
            OnPathClosedByAGVImpactDetecting?.Invoke(this, points);
        }

        internal void UpdatePartRegistedPointInfo(List<MapPoint> RegistPointByPart)
        {
            while (true)
            {
                Thread.Sleep(5000);
                Dictionary<string, string> RegistAreaFromPartsDB = TrafficControl.PartsAGVSHelper.QueryAGVSRegistedAreaName().Result;
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
                if (RegistPointByPart.Count == 0)
                {
                    break;
                }

            }
        }

        internal MapPoint GetNextPointToGo(MapPoint currentMapPoint, bool includesVituralPoint = true)
        {
            try
            {
                return EntirePathPlan.First(pt => pt.TagNumber != currentMapPoint.TagNumber && (includesVituralPoint ? true : !pt.IsVirtualPoint) && EntirePathPlan.IndexOf(pt) > EntirePathPlan.IndexOf(currentMapPoint));
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

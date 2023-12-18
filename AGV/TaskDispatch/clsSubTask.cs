using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using Microsoft.AspNetCore.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using VMSystem.Tools;
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
            try
            {
                ExecuteOrderAGVName = order.DesignatedAGVName;
                var TargetAGVItem = VMSManager.AllAGV.First(item => item.Name == ExecuteOrderAGVName);
                PathFinder pathFinder = new PathFinder();
                var PathNoConsiderAGV = pathFinder.FindShortestPath(StaMap.Map, Source, Destination); //不考慮AGV阻擋的路徑

                var optimiedPath = pathFinder.FindShortestPath(StaMap.Map, Source, Destination, new PathFinderOption
                {
                    ConstrainTags = VMSManager.GetAGVListExpectSpeficAGV(ExecuteOrderAGVName).Select(agv => agv.currentMapPoint.TagNumber).ToList()
                }); //考慮AGV組黨後計算出的路徑

                optimiedPath =optimiedPath == null ? PathNoConsiderAGV : optimiedPath;
                if (EntirePathPlan.Count !=0&& EntirePathPlan.First()== Source&&EntirePathPlan.Last() == Destination)
                {//如果已經下過前半段的走行任務，後半段的任務要直接沿用原本的路徑
                    optimiedPath.stations = EntirePathPlan;
                }
                else
                {
                    EntirePathPlan = optimiedPath.stations;
                }
                var otherAGVList = VMSManager.GetAGVListExpectSpeficAGV(ExecuteOrderAGVName);
                List<IAGV> agv_too_near_from_path = new List<IAGV>();

                var Dict_NearPoint = this.Action == ACTION_TYPE.None ? GetNearTargetMapPointOfPathByPointDistance(optimiedPath.stations, TargetAGVItem.options.VehicleLength / 100.0) : new Dictionary<int, List<MapPoint>>();
                if (this.Action == ACTION_TYPE.Unpark)
                {
                    if (Dict_NearPoint.ContainsKey(Source.TagNumber))
                    {
                        Dict_NearPoint.Remove(Source.TagNumber);
                    }
                }
                TargetAGVItem.taskDispatchModule.Dict_PathNearPoint = Dict_NearPoint;

                try
                {
                    List<MapPoint> PathPointWithRegistNearPoint = StaMap.GetRegistedPointWithNearPointOfPath(optimiedPath.stations, Dict_NearPoint, ExecuteOrderAGVName);
                    List<MapPoint> regitedPoints = StaMap.GetRegistedPointsOfPath(optimiedPath.stations, ExecuteOrderAGVName);
                    regitedPoints.AddRange(PathPointWithRegistNearPoint); 
                    if (Action == ACTION_TYPE.None && NavigationTools.TryFindInterferenceAGVOfPoint(TargetAGVItem, optimiedPath.stations, out var interferenceMapPoints))
                    {
                        regitedPoints.AddRange(interferenceMapPoints.Select(di => di.Key).ToList());
                        regitedPoints= regitedPoints.Distinct().ToList();
                    }

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
                            var RegistPointInfo = new clsPointRegistInfo();
                            StaMap.RegistPoint("Parts", item, out string _msg);
                        }
                        Task.Run(() => UpdatePartRegistedPointInfo(PartsRegistedPoints.ToList()));
                    }

                    regitedPoints.AddRange(otherAGVList.Select(agv => agv.currentMapPoint));
                    regitedPoints = regitedPoints.Where(pt => pt != null).Distinct().ToList();
                    var RegistedMapPoint = optimiedPath.stations.Where(item => regitedPoints.Contains(item));
                    if (RegistedMapPoint.Any()) //有點位被註冊
                    {
                        //var index_of_registed_pt = optimiedPath.stations.FindIndex(pt => pt.TagNumber == regitedPoints.First().TagNumber);
                        var index_of_registed_pt = optimiedPath.stations.FindIndex(pt => pt == RegistedMapPoint.First());
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


                    if (optimiedPath.stations.Count != 0)
                    {
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


                        LastStopPoint = optimiedPath.stations.Last();
                        var RegistPath = pathFinder.FindShortestPath(StaMap.Map, TargetAGVItem.currentMapPoint, LastStopPoint);
                        var StartIndex = optimiedPath.stations.IndexOf(optimiedPath.stations.FirstOrDefault(pt => pt.TagNumber == TargetAGVItem.currentMapPoint.TagNumber));
                        StartIndex = StartIndex == -1 ? 0 : StartIndex;
                        List<MapPoint> List_TryToRegist = new List<MapPoint>();
                        for (int i = StartIndex; i < optimiedPath.stations.Count; i++)
                        {
                            List_TryToRegist.Add(optimiedPath.stations[i]);
                        }
                        LOG.INFO($"TaskRegist {ExecuteOrderAGVName}, {string.Join(",", List_TryToRegist.Select(point => point.TagNumber))}");
                        StaMap.RegistPoint(ExecuteOrderAGVName, List_TryToRegist, out string Error);
                    }
                    else//無路可走
                    {
                        var current_point_index = EntirePathPlan.FindIndex(pt => pt.TagNumber == TargetAGVItem.currentMapPoint.TagNumber);
                        MapPoint[] path_gen = new MapPoint[current_point_index + 1];
                        Array.Copy(EntirePathPlan.ToArray(), 0, path_gen, 0, path_gen.Length);
                        optimiedPath.stations = path_gen.ToList();
                    }
                }
                catch (Exception ex)
                {
                    LOG.ERROR(ex);
                    throw ex;
                }


                var TrajectoryToExecute = PathFinder.GetTrajectory(StaMap.Map.Name, optimiedPath.stations).ToArray();

                Int32.TryParse(order.From_Station, out var fromTag);
                Int32.TryParse(order.To_Station, out var toTag);
                var DestineData = StaMap.GetPointByTagNumber(toTag);
                DownloadData = new clsTaskDownloadData
                {
                    Action_Type = Action,
                    Destination = Destination.TagNumber,
                    Task_Name = order.TaskName,
                    Task_Simplex = $"{order.TaskName}-{sequence}",
                    Task_Sequence = sequence,
                    Station_Type = Destination.StationType,
                    CST = new clsCST[1] { new clsCST { CST_ID = CarrierID } },
                    OrderInfo = new clsTaskDownloadData.clsOrderInfo
                    {
                        ActionName = order.Action,
                        SourceName = StaMap.GetStationNameByTag(fromTag),
                        DestineName = StaMap.GetStationNameByTag(toTag),
                    },
                    Height = order.Height
                };

                if (TrajectoryToExecute.Length == 0)
                {

                    LOG.Critical($"From {fromTag} To {toTag}, Get Trajectory Length = 0, Order Data {order.ToJson()}");
                }
                double stopAngle = order.Action == ACTION_TYPE.None ? CalculationStopAngle(TrajectoryToExecute, isSegment) : DestineStopAngle;

                TrajectoryToExecute.Last().Theta = stopAngle;

                if (Action == ACTION_TYPE.None)
                    DownloadData.Trajectory = TrajectoryToExecute;
                else
                {
                    DownloadData.Homing_Trajectory = TrajectoryToExecute;
                }
                lastPt = TrajectoryToExecute.Last();
                var _lastPtTag = lastPt.Point_ID;
                //RegistConflicPoints(_lastPtTag, agv_too_near_from_path);  }
            }
            catch (Exception ex)
            {
                LOG.Critical(ex);
            }
        }

        private Dictionary<int, List<MapPoint>> GetNearTargetMapPointOfPathByPointDistance(List<MapPoint> List_path, double Distance = 1)
        {
            Dictionary<int, List<MapPoint>> Dict_OutputData = new Dictionary<int, List<MapPoint>>();
            foreach (var item in List_path)
            {
                var Dict_NearPoint = StaMap.Dict_AllPointDistance[item.TagNumber].Where(eachpoint => eachpoint.Value < Distance);
                if (Dict_NearPoint.Count() > 0)
                {
                    var List_NearPoint = Dict_NearPoint.Select(eachpoint => StaMap.GetPointByTagNumber(eachpoint.Key)).ToList();
                    Dict_OutputData.Add(item.TagNumber, List_NearPoint);
                }
                else
                {
                    Dict_OutputData.Add(item.TagNumber, new List<MapPoint>());
                }

            }
            return Dict_OutputData;
        }

        private double CalculationStopAngle(clsMapPoint[] trajectoryToExecute, bool _IsSeqmentTrajectory)
        {
            clsMapPoint finalPt = trajectoryToExecute.Last();
            System.Drawing.PointF finalPtPointF = new System.Drawing.PointF((float)finalPt.X, (float)finalPt.Y);
            System.Drawing.PointF finalCountDown2PtPointF = new System.Drawing.PointF();

            if (_IsSeqmentTrajectory)
            {
                if (EntirePathPlan.Count == 1)
                    return trajectoryToExecute.Last().Theta;
                var indexOfLastTwoPt = EntirePathPlan.FindIndex(pt => pt.TagNumber == trajectoryToExecute.Last().Point_ID) + 1;
                finalCountDown2PtPointF.X = (float)EntirePathPlan[indexOfLastTwoPt].X;
                finalCountDown2PtPointF.Y = (float)EntirePathPlan[indexOfLastTwoPt].Y;
                return NavigationTools.CalculationForwardAngle(finalPtPointF, finalCountDown2PtPointF);
            }
            else
            {

                var countDown2PtIndex = trajectoryToExecute.ToList().IndexOf(finalPt) - 1;
                if (countDown2PtIndex < 0)
                    return DestineStopAngle;
                clsMapPoint countDown2Pt = trajectoryToExecute[countDown2PtIndex];
                finalCountDown2PtPointF.X = (float)countDown2Pt.X;
                finalCountDown2PtPointF.Y = (float)countDown2Pt.Y;
                return NavigationTools.CalculationForwardAngle(finalCountDown2PtPointF, finalPtPointF);

            }

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
                        StaMap.UnRegistPoint("Parts", item.TagNumber, out Error);
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

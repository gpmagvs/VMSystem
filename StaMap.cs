using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using NLog;
using VMSystem.AGV;
using VMSystem.Dispatch.Regions;
using VMSystem.VMS;

namespace VMSystem
{
    public static class MapExtensions
    {
        public static MapCircleArea GetCircleArea(this MapPoint point, ref IAGV refAGv, double sizeRatio = 1)
        {
            float length = (float)(refAGv.options.VehicleLength / 100.0 * sizeRatio);
            float width = (float)(refAGv.options.VehicleWidth / 100.0 * sizeRatio);
            return new MapCircleArea(length, width, new System.Drawing.PointF((float)point.X, (float)point.Y));
        }

        /// <summary>
        /// 取得點位所在的區域
        /// </summary>
        /// <param name="pt"></param>
        /// <returns></returns>
        public static MapRegion GetRegion(this MapPoint pt)
        {
            return pt.GetRegion(StaMap.Map);
        }
    }
    public class StaMap
    {
        public static Map Map { get; set; }
        private static string MapFile => AGVSConfigulator.SysConfigs.MapConfigs.MapFileFullName;
        internal static event EventHandler<int> OnTagUnregisted;
        public static Dictionary<int, clsPointRegistInfo> RegistDictionary = new Dictionary<int, clsPointRegistInfo>();
        public static Dictionary<int, Dictionary<int, double>> Dict_AllPointDistance = new Dictionary<int, Dictionary<int, double>>();
        public static event EventHandler<List<MapPoint>> OnPointsDisabled;
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static SemaphoreSlim _DisablePtChangeDectectProcSemaphore = new SemaphoreSlim(1, 1);
        public static void Download()
        {
            var _oriPoints = Map?.Points.Values.Clone().ToList();
            Map = MapManager.LoadMapFromFile(false, false).Clone();
            Dict_AllPointDistance = GetAllPointDistance(Map, 1);
            PathFinder.defaultMap = Map;
            DisablePointsChangedDetecter(_oriPoints);
            RegionManager.Initialze();
            logger.Info($"圖資載入完成:{Map.Name} ,Version:{Map.Note}");
        }

        private static async Task DisablePointsChangedDetecter(List<MapPoint> oriPoints)
        {
            try
            {
                await _DisablePtChangeDectectProcSemaphore.WaitAsync();
                if (oriPoints == null)
                    return;
                var _newPoints = Map.Points.Values.Clone().ToList();
                var oriPtEnableStatus = oriPoints.ToDictionary(pt => pt, pt => pt.Enable);
                var newPtEnableStatus = _newPoints.ToDictionary(pt => pt, pt => pt.Enable);
                var changeToDisablePoints = newPtEnableStatus.Where(pt => pt.Value == false && oriPtEnableStatus.First(_pt => _pt.Key.TagNumber == pt.Key.TagNumber).Value == true)
                                                                .Select(pt => pt.Key); ;
                if (changeToDisablePoints.Any())
                {
                    logger.Trace($"Detect Tags: {string.Join(",", changeToDisablePoints.Select(pt => pt.TagNumber))} changed to DISABLE");
                    OnPointsDisabled?.Invoke("", changeToDisablePoints.ToList());
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            finally
            {
                _DisablePtChangeDectectProcSemaphore.Release();
            }
        }

        internal static List<MapPoint> GetParkableStations()
        {
            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.ToList().FindAll(sta => sta.IsParking);
            return chargeableStations;
        }
        internal static List<MapPoint> GetChargeableStations(IAGV TargetAGV = null)
        {
            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.Where(sta => sta.Enable && sta.IsChargeAble()).ToList();
            if (TargetAGV == null)
            {
                return chargeableStations;
            }
            var AGVuseableChargePoint = StaMap.GetPointByTagNumber(TargetAGV.options.List_ChargeStation);
            if (AGVuseableChargePoint.Count == 0)
            {
                return chargeableStations;
            }
            chargeableStations = chargeableStations.Intersect(AGVuseableChargePoint).ToList();
            return chargeableStations;
        }

        internal static List<MapPoint> GetAvoidStations()
        {
            if (Map == null)
                Download();

            var avoidStations = Map.Points.Values.ToList().FindAll(sta => sta.IsAvoid);
            return avoidStations;
        }

        internal static string GetBayNameByMesLocation(string location)
        {
            var Bay = Map.Bays.FirstOrDefault(bay => bay.Value.Points.Contains(location));
            if (Bay.Key != null)
                return Bay.Key;
            else
                return "Unknown_Bay";
        }
        internal static bool TryGetPointByTagNumber(int tagNumber, out MapPoint point)
        {
            point = Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == tagNumber);
            return point != null;
        }
        internal static MapPoint GetPointByTagNumber(int tagNumber)
        {
            var point = Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == tagNumber);
            if (point == null)
            {
                point = new MapPoint
                {
                    TagNumber = tagNumber,
                    Name = tagNumber.ToString()
                };
            }
            return point.Clone();
        }

        internal static List<MapPoint> GetPointByTagNumber(List<int> List_TagNumber)
        {
            if (List_TagNumber == null)
            {
                List_TagNumber = new List<int>();
            }
            var ReturnData = List_TagNumber.Select(item => GetPointByTagNumber(item)).ToList();
            return ReturnData.Clone();
        }

        internal static MapPoint GetPointByName(string name)
        {
            var point = Map.Points.FirstOrDefault(pt => pt.Value.Graph.Display == name);
            if (point.Value != null)
                return point.Value.Clone();
            return null;
        }
        internal static MapPoint GetPointByIndex(int index)
        {
            var point = Map.Points.FirstOrDefault(pt => pt.Key == index);
            if (point.Value != null)
                return point.Value.Clone();
            return null;
        }

        internal static int GetIndexOfPoint(MapPoint mapPoint)
        {
            try
            {
                int index = Map.Points.FirstOrDefault(k => k.Value.TagNumber == mapPoint.TagNumber).Key;
                return index;
            }
            catch (Exception ex)
            {
                return -1;
            }
        }
        internal static bool RegistPoint(string Name, IEnumerable<int> Tags, out string error_message)
        {
            return RegistPoint(Name, Tags.Select(tag => GetPointByTagNumber(tag)), out error_message);
        }
        internal static bool RegistPoint(string Name, IEnumerable<MapPoint> mapPoints, out string error_message)
        {
            error_message = string.Empty;
            foreach (var item in mapPoints)
            {
                if (!RegistPoint(Name, item, out error_message))
                {
                    return false;
                }

            }
            return true;
        }
        internal static bool RegistPointBySystem(MapPoint mapPoint, out string error_message, string AGVName = "")
        {
            return RegistPoint("System", mapPoint, out error_message, true);
        }

        private static void RegistNearPoints(string VehicleName, MapPoint CenterPoints)
        {
            IAGV registAgv = VMSManager.GetAGVByName(VehicleName);

            var centerCircleArea = CenterPoints.GetCircleArea(ref registAgv);

            var normalPoints = Map.Points.Values.Where(pt => pt.StationType == MapPoint.STATION_TYPE.Normal);

            var conflicPoints = normalPoints.Where(pt => pt.GetCircleArea(ref registAgv).IsIntersectionTo(centerCircleArea));

            foreach (var point in conflicPoints)
            {
                bool success = RegistPoint(VehicleName, point, out var errMsg, registNearPoints: false);
                if (!success)
                {

                }
                else
                {
                    RegistDictionary[point.TagNumber].NearRegistInfo.IsRegisted = true;
                    RegistDictionary[point.TagNumber].NearRegistInfo.RegistByPointTag = CenterPoints.TagNumber;
                }
            }
            //registAgv.AGVRotaionGeometry
        }

        internal static bool RegistPoint(string Name, MapPoint mapPoint, out string error_message, bool IsBySystem = false, bool registNearPoints = true)
        {
            try
            {
                _registSemaphore.Wait();
                error_message = string.Empty;
                var TagNumber = mapPoint.TagNumber;
                if (IsBySystem)
                {
                    mapPoint.RegistInfo = new clsPointRegistInfo("System");
                    RegistDictionary.Remove(TagNumber, out clsPointRegistInfo _info);
                    RegistDictionary.Add(TagNumber, mapPoint.RegistInfo);
                    //logger.Trace($"{Name} Regist Tag {TagNumber}");
                    return true;
                }
                if (RegistDictionary.ContainsKey(TagNumber))
                {
                    string registerName = RegistDictionary[TagNumber].RegisterAGVName;
                    bool _success = registerName == Name;
                    if (_success)
                    {

                        //logger.Trace($"{RegistDictionary.ToJson()}");

                        //TrafficControl.PartsAGVSHelper.UnRegistStationRequestToAGVS(new List<string>() { mapPoint.Graph.Display });
                        //logger.Trace($"{Name} Regist Tag {TagNumber}");
                    }
                    else
                    {
                        error_message = $"Tag {TagNumber} is registed by [{registerName}]";
                        //logger.Trace($"{Name} Regist Tag {TagNumber} Fail:{error_message}");

                    }
                    return _success;
                }
                else
                {
                    mapPoint.RegistInfo = new clsPointRegistInfo(Name);
                    bool registSuccess = RegistDictionary.TryAdd(TagNumber, mapPoint.RegistInfo);
                    if (registSuccess)
                    {
                        logger.Trace($"{Name} Regist Tag {TagNumber}");
                        //if (registNearPoints)
                        //    RegistNearPoints(Name, mapPoint);
                    }
                    else
                    {
                        //logger.Trace($"{Name} Regist Tag {TagNumber} Fail");
                    }
                    return registSuccess;
                }
            }
            catch (Exception ex)
            {
                error_message = ex.Message;
                return false;
            }
            finally
            {
                _registSemaphore.Release();
            }

        }
        internal static async Task<(bool success, string error_message)> UnRegistPointBySystem(MapPoint mapPoint)
        {
            return await UnRegistPoint("System", mapPoint, true);
        }

        internal static async Task<(bool success, string error_message)> UnRegistPoints(string Name, IEnumerable<int> TagNumbers, bool isBySystem = false)
        {
            foreach (var tag in TagNumbers)
            {
                await UnRegistPoint(Name, tag, isBySystem);
            }
            return (true, "");
        }
        private static SemaphoreSlim _unregistSemaphore = new SemaphoreSlim(1, 1);
        private static SemaphoreSlim _registSemaphore = new SemaphoreSlim(1, 1);
        internal static async Task<(bool success, string error_message)> UnRegistPoint(string Name, int TagNumber, bool IsBySystem = false)
        {
            try
            {
                await _unregistSemaphore.WaitAsync();
                IAGV agv = VMSManager.GetAGVByName(Name);
                if (agv == null && !IsBySystem)
                    return (false, "AGV Entity NULL");
                var registedPointsByConflic = agv?.RegistedByConflicCheck;
                var mapPoint = StaMap.GetPointByTagNumber(TagNumber);
                if (IsBySystem)
                {
                    RegistDictionary.Remove(TagNumber, out var _);
                    mapPoint.RegistInfo = new clsPointRegistInfo();
                    logger.Trace($"{Name} UnRegist Tag {TagNumber}");
                    return (true, "");
                }
                if (RegistDictionary.ContainsKey(TagNumber))
                {
                    //var mapPt = registedPointsByConflic.FirstOrDefault(pt => pt.TagNumber == TagNumber);
                    //if (mapPt != null && mapPt.GetCircleArea(ref agv, 0.5).IsIntersectionTo(agv.AGVRotaionGeometry))
                    //{
                    //    var msg = $"Too Near Point cannot unregist-{Name}";
                    //    logger.Trace(msg);
                    //    return (false, msg);
                    //}

                    var error_message = "";
                    string registerName = RegistDictionary[TagNumber].RegisterAGVName;
                    bool allow_remove = registerName == Name;
                    if (allow_remove)
                    {


                        mapPoint.RegistInfo = new clsPointRegistInfo();
                        RegistDictionary.Remove(TagNumber, out var _);
                        OnTagUnregisted?.Invoke("", TagNumber);
                        TrafficControl.PartsAGVSHelper.UnRegistStationRequestToAGVS(new List<string>() { mapPoint.Graph.Display });
                        logger.Trace($"{Name} UnRegist Tag {TagNumber}");

                        //var nearRegistedPoints = RegistDictionary.Where(pair => pair.Value.NearRegistInfo.RegistByPointTag == TagNumber).Select(p => p.Key);

                        //if (nearRegistedPoints.Any())
                        //{
                        //    foreach (var tag in nearRegistedPoints)
                        //    {
                        //        RegistDictionary.Remove(tag);
                        //    }
                        //}

                    }
                    else
                    {
                        error_message = $"Tag {TagNumber} cannont be unregisted because it registed by [{registerName}]";
                        logger.Trace($"{Name} UnRegist Tag {TagNumber} Fail: {error_message}");
                    }

                    return (allow_remove, error_message);
                }
                else
                {
                    OnTagUnregisted?.Invoke("", TagNumber);
                    //logger.Trace($"{Name} UnRegist Tag {TagNumber}");
                    return (true, "");
                }
            }
            catch (Exception ex)
            {
                var error_message = ex.Message;
                logger.Warn($"{Name} UnRegist Tag {TagNumber} Fail : {error_message}");
                return (false, error_message);
            }
            finally
            {
                _unregistSemaphore.Release();
            }

        }

        internal static async Task<(bool success, string error_message)> UnRegistPoint(string Name, MapPoint mapPoint, bool IsBySystem = false)
        {
            return await UnRegistPoint(Name, mapPoint.TagNumber, IsBySystem);
        }

        internal static bool CheckTagExistOnMap(int currentTag)
        {
            return Map.Points.Select(pt => pt.Value.TagNumber).Contains(currentTag);
        }

        internal static List<MapPoint> GetRegistedPointWithNearPointOfPath(List<MapPoint> OriginPath, Dictionary<int, List<MapPoint>> DictAllNearPoint, string NowAGV)
        {
            List<MapPoint> OutputData = new List<MapPoint>();
            var RegistDictWithoutNowAGV = RegistDictionary.Where(item => item.Value.RegisterAGVName != NowAGV).ToDictionary(item => item.Key, item => item.Value);
            foreach (var item in DictAllNearPoint)
            {
                if (item.Value.Any(nearPoint => RegistDictWithoutNowAGV.ContainsKey(nearPoint.TagNumber)))
                {
                    OutputData.Add(StaMap.GetPointByTagNumber(item.Key));
                }
            }
            return OutputData.OrderBy(pt => OriginPath.IndexOf(pt)).ToList();
        }

        internal static List<MapPoint> GetRegistedPointsOfPath(List<MapPoint> path_to_nav, string navigating_agv_name)
        {
            var registPoints = RegistDictionary.Where(kp => kp.Value.RegisterAGVName != navigating_agv_name).Select(kp => StaMap.GetPointByTagNumber(kp.Key));
            IEnumerable<MapPoint> commonItems = registPoints.Intersect(path_to_nav, new MapPointComparer());
            return commonItems.OrderBy(pt => path_to_nav.IndexOf(pt)).ToArray().ToList();
        }

        internal static string GetStationNameByTag(int tag)
        {
            var point = Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == tag);
            return point == null ? tag + "" : point.Graph.Display;
        }

        internal static List<MapPoint> GetAllRegistedPointsByName(string name)
        {
            return Map.Points.Values.Where(pt => pt.RegistInfo != null).Where(pt => pt.RegistInfo.RegisterAGVName == name).ToList();
        }

        internal static async Task UnRegistPoints(string name, List<MapPoint> unRegistList)
        {
            foreach (var point in unRegistList)
            {
                await UnRegistPoint(name, point);
            }
        }

        internal static bool IsMapPointRegisted(MapPoint waitingPoint, string queryer_name)
        {
            if (RegistDictionary.TryGetValue(waitingPoint.TagNumber, out var result))
            {
                return result.RegisterAGVName != queryer_name;
            }
            else
                return false;
        }

        internal static async Task<bool> UnRegistPointsOfAGVRegisted(IAGV agv)
        {
            try
            {
                var registed_tag_except_current_tag = RegistDictionary.Where(kp => kp.Value.RegisterAGVName == agv.Name && kp.Key != agv.states.Last_Visited_Node).Select(kp => kp.Key).ToList();
                var result = await UnRegistPoints(agv.Name, registed_tag_except_current_tag);
                if (result.success && registed_tag_except_current_tag.Any())
                {
                    NotifyServiceHelper.SUCCESS($"{agv.Name} Unregist tags={(string.Join(",", registed_tag_except_current_tag))}");
                }
                return result.success;
            }
            catch (Exception ex)
            {
                logger.Fatal(ex);
                return false;
            }
        }

        internal static bool GetPointRegisterName(int tagNumber, out string agvName)
        {
            agvName = string.Empty;
            if (!RegistDictionary.TryGetValue(tagNumber, out var _rinfo))
                return false;
            agvName = _rinfo.RegisterAGVName;
            return agvName != null;
        }

        internal static bool GetNearPointRegisterName(int tagNumber, string TargetAGV, out string AGVName, out int NearPointTag)
        {
            AGVName = string.Empty;
            NearPointTag = -1;
            var TargetPoint = GetPointByTagNumber(tagNumber);
            var AGVItem = VMSManager.AllAGV.First(item => item.Name == TargetAGV);
            //var List_NearPointTag = TargetPoint.Target.Where(item => (item.Value * 100) < 100).Select(item => item.Key);
            var List_NearPointTag = Dict_AllPointDistance[tagNumber].Where(item => item.Value < AGVItem.options.VehicleLength / 100).Select(item => item.Key);
            foreach (var item in List_NearPointTag)
            {
                var MapPointData = GetPointByTagNumber(item);
                if (RegistDictionary.TryGetValue(MapPointData.TagNumber, out var _RegistInfo))
                {
                    if (_RegistInfo.RegisterAGVName == TargetAGV) //自己跟自己不卡控
                        continue;
                    AGVName = _RegistInfo.RegisterAGVName;
                    NearPointTag = item;
                    return true;
                }
            }
            return false;
        }

        internal static Dictionary<int, Dictionary<int, double>> GetAllPointDistance(Map MapData, double DistanceLimit)
        {
            Dictionary<int, Dictionary<int, double>> Dict_OutputData = new Dictionary<int, Dictionary<int, double>>();
            foreach (var TargetPoint in MapData.Points)
            {
                var TargetPointData = TargetPoint.Value;
                Dict_OutputData.Add(TargetPointData.TagNumber, new Dictionary<int, double>());

                foreach (var RelatePoint in MapData.Points)
                {
                    if (RelatePoint.Key == TargetPoint.Key)
                    {
                        continue;
                    }
                    var RelatePointData = RelatePoint.Value;
                    var Distance = CalculateDistance(TargetPointData.X, RelatePointData.X, TargetPointData.Y, RelatePointData.Y);
                    Dict_OutputData[TargetPointData.TagNumber].Add(RelatePointData.TagNumber, Distance);
                }
            }
            return Dict_OutputData;
        }

        internal static List<int> GetNearPointListByPointAndDistance(int TagNumber, double DistanceLimit)
        {
            if (!Dict_AllPointDistance.ContainsKey(TagNumber))
                return new List<int>();

            return Dict_AllPointDistance[TagNumber].Where(item => item.Value < DistanceLimit).Select(item => item.Key).ToList();
        }

        internal static List<int> GetNearPointListByPathAndDistance(List<int> List_PathTags, double DistanceLimit)
        {
            List<int> OutputData = new List<int>();
            foreach (var item in List_PathTags)
            {
                OutputData.AddRange(GetNearPointListByPointAndDistance(item, DistanceLimit));
            }
            OutputData = OutputData.Distinct().ToList();
            return OutputData;
        }

        internal static double CalculateDistance(double X1, double X2, double Y1, double Y2)
        {
            return Math.Pow(Math.Pow(X1 - X2, 2) + Math.Pow(Y1 - Y2, 2) * 1.0, 0.5);
        }

        internal static List<MapPoint> GetNoStopPointsByAGVModel(clsEnums.AGV_TYPE model)
        {
            List<int> tags = GetNoStopTagsByAGVModel(model);
            return tags.Select(tag => GetPointByTagNumber(tag)).ToList();
        }
        internal static List<int> GetNoStopTagsByAGVModel(clsEnums.AGV_TYPE model)
        {
            List<int> tags = new List<int>();
            switch (model)
            {
                case clsEnums.AGV_TYPE.FORK:
                    tags = Map.TagNoStopOfForkAGV.Clone();
                    break;
                case clsEnums.AGV_TYPE.YUNTECH_FORK_AGV:
                    break;
                case clsEnums.AGV_TYPE.INSPECTION_AGV:
                    break;
                case clsEnums.AGV_TYPE.SUBMERGED_SHIELD:
                    tags = Map.TagNoStopOfSubmarineAGV.Clone();
                    break;
                case clsEnums.AGV_TYPE.SUBMERGED_SHIELD_Parts:
                    break;
                case clsEnums.AGV_TYPE.Any:
                    break;
                default:
                    break;
            }
            return tags;
        }
        private static SemaphoreSlim removePathSemaphoreSlim = new SemaphoreSlim(1, 1);
        internal static async Task<(bool confirmed, MapPath path)> TryRemovePathDynamic(MapPoint fromPt, MapPoint toPt)
        {
            try
            {
                await removePathSemaphoreSlim.WaitAsync();
                string pathID = $"{GetIndexOfPoint(fromPt)}_{GetIndexOfPoint(toPt)}";
                var path = Map.Segments.FirstOrDefault(_path => _path.PathID == pathID);
                if (path == null)
                    return (false, null);
                bool removed = Map.Segments.Remove(path);
                return (removed, path);

            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                removePathSemaphoreSlim.Release();

            }
        }

        internal static bool AddPathDynamic(MapPath path)
        {
            try
            {
                var existPath = Map.Segments.FirstOrDefault(_path => _path.PathID == path.PathID);
                if (existPath != null)
                    Map.Segments.Remove(existPath);
                Map.Segments.Add(path);
                NotifyServiceHelper.INFO($"路徑 {path.ToString()} 已恢復通行");
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public class MapPointComparer : IEqualityComparer<MapPoint>
        {
            public bool Equals(MapPoint x, MapPoint y)
            {
                if (x == null || y == null)
                    return false;

                return x.TagNumber == y.TagNumber;
            }

            public int GetHashCode(MapPoint obj)
            {
                return obj.TagNumber.GetHashCode();
            }
        }
    }
}

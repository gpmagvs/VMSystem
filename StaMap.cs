using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.MAP;
using Newtonsoft.Json;
using System.IO.Compression;
using AGVSystemCommonNet6.Configuration;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using System;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using VMSystem.AGV;
using VMSystem.VMS;
using AGVSystemCommonNet6.Log;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Runtime;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace VMSystem
{
    public class StaMap
    {
        public static Map Map { get; set; }
        private static string MapFile => AGVSConfigulator.SysConfigs.MapConfigs.MapFileFullName;
        internal static event EventHandler<int> OnTagUnregisted;
        public static Dictionary<int, clsPointRegistInfo> RegistDictionary = new Dictionary<int, clsPointRegistInfo>();
        public static Dictionary<int, Dictionary<int, double>> Dict_AllPointDistance = new Dictionary<int, Dictionary<int, double>>();

        public static void Download()
        {
            Map = MapManager.LoadMapFromFile(false, false);
            Dict_AllPointDistance = GetAllPointDistance(Map, 1);
            Console.WriteLine($"圖資載入完成:{Map.Name} ,Version:{Map.Note}");
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
            return point;
        }

        internal static List<MapPoint> GetPointByTagNumber(List<int> List_TagNumber)
        {
            if (List_TagNumber == null)
            {
                List_TagNumber = new List<int>();
            }
            var ReturnData = List_TagNumber.Select(item => GetPointByTagNumber(item)).ToList();
            return ReturnData;
        }

        internal static MapPoint GetPointByName(string name)
        {
            var point = Map.Points.FirstOrDefault(pt => pt.Value.Graph.Display == name);
            if (point.Value != null)
                return point.Value;
            return null;
        }
        internal static MapPoint GetPointByIndex(int index)
        {
            var point = Map.Points.FirstOrDefault(pt => pt.Key == index);
            if (point.Value != null)
                return point.Value;
            return null;
        }

        internal static int GetIndexOfPoint(MapPoint mapPoint)
        {
            int index = Map.Points.FirstOrDefault(k => k.Value.TagNumber == mapPoint.TagNumber).Key;
            return index;
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
                RegistPoint(Name, item, out error_message);

            }
            return true;
        }
        internal static bool RegistPointBySystem(MapPoint mapPoint, out string error_message, string AGVName = "")
        {
            return RegistPoint("System", mapPoint, out error_message, true);
        }
        internal static bool RegistPoint(string Name, MapPoint mapPoint, out string error_message, bool IsBySystem = false)
        {
            error_message = string.Empty;
            var TagNumber = mapPoint.TagNumber;
            if (IsBySystem)
            {
                mapPoint.RegistInfo = new clsPointRegistInfo("System");
                RegistDictionary.Remove(TagNumber, out clsPointRegistInfo _info);
                RegistDictionary.Add(TagNumber, mapPoint.RegistInfo);
                //LOG.TRACE($"{Name} Regist Tag {TagNumber}");
                return true;
            }
            if (RegistDictionary.ContainsKey(TagNumber))
            {
                string registerName = RegistDictionary[TagNumber].RegisterAGVName;
                bool _success = registerName == Name;
                if (_success)
                {
                    //LOG.TRACE($"{RegistDictionary.ToJson()}");

                    //TrafficControl.PartsAGVSHelper.UnRegistStationRequestToAGVS(new List<string>() { mapPoint.Graph.Display });
                    //LOG.TRACE($"{Name} Regist Tag {TagNumber}");
                }
                else
                {
                    error_message = $"Tag {TagNumber} is registed by [{registerName}]";
                    //LOG.TRACE($"{Name} Regist Tag {TagNumber} Fail:{error_message}");

                }
                return _success;
            }
            else
            {
                mapPoint.RegistInfo = new clsPointRegistInfo(Name);
                bool registSuccess = RegistDictionary.TryAdd(TagNumber, mapPoint.RegistInfo);
                if (registSuccess)
                    LOG.TRACE($"{Name} Regist Tag {TagNumber}");
                else
                {
                    //LOG.TRACE($"{Name} Regist Tag {TagNumber} Fail");
                }
                return registSuccess;
            }
        }
        internal static bool UnRegistPointBySystem(MapPoint mapPoint, out string error_message)
        {
            return UnRegistPoint("System", mapPoint, out error_message, true);
        }

        internal static bool UnRegistPoints(string Name, IEnumerable<int> TagNumbers, out string errorMsg, bool isBySystem = false)
        {
            errorMsg = string.Empty;
            foreach (var tag in TagNumbers)
            {
                UnRegistPoint(Name, tag, out errorMsg, isBySystem);
            }
            return true;
        }
        internal static bool UnRegistPoint(string Name, int TagNumber, out string error_message, bool IsBySystem = false)
        {
            error_message = string.Empty;
            lock (RegistDictionary)
            {
                var mapPoint = StaMap.GetPointByTagNumber(TagNumber);
                if (IsBySystem)
                {
                    RegistDictionary.Remove(TagNumber, out var _);
                    mapPoint.RegistInfo = new clsPointRegistInfo();
                    LOG.TRACE($"{Name} UnRegist Tag {TagNumber}");
                    return true;
                }
                if (RegistDictionary.ContainsKey(TagNumber))
                {
                    string registerName = RegistDictionary[TagNumber].RegisterAGVName;
                    bool allow_remove = registerName == Name;
                    if (allow_remove)
                    {
                        mapPoint.RegistInfo = new clsPointRegistInfo();
                        lock (RegistDictionary)
                        {
                            RegistDictionary.Remove(TagNumber, out var _);
                            OnTagUnregisted?.Invoke("", TagNumber);
                            TrafficControl.PartsAGVSHelper.UnRegistStationRequestToAGVS(new List<string>() { mapPoint.Graph.Display });
                            LOG.TRACE($"{Name} UnRegist Tag {TagNumber}");

                            //LOG.TRACE($"{RegistDictionary.ToJson()}");
                        }
                    }
                    else
                    {
                        error_message = $"Tag {TagNumber} cannont be unregisted because it registed by [{registerName}]";
                        LOG.TRACE($"{Name} UnRegist Tag {TagNumber} Fail: {error_message}");
                    }

                    return allow_remove;
                }
                else
                {
                    OnTagUnregisted?.Invoke("", TagNumber);
                    //LOG.TRACE($"{Name} UnRegist Tag {TagNumber}");
                    return true;
                }
            }

        }

        internal static bool UnRegistPoint(string Name, MapPoint mapPoint, out string error_message, bool IsBySystem = false)
        {
            return UnRegistPoint(Name, mapPoint.TagNumber, out error_message, IsBySystem);
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

        internal static void UnRegistPoints(string name, List<MapPoint> unRegistList)
        {
            foreach (var point in unRegistList)
            {
                UnRegistPoint(name, point, out string errmsg);
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

        internal static bool UnRegistPointsOfAGVRegisted(IAGV agv)
        {
            try
            {
                var registed_tag_except_current_tag = RegistDictionary.Where(kp => kp.Value.RegisterAGVName == agv.Name && kp.Key != agv.states.Last_Visited_Node).Select(kp => kp.Key).ToList();
                UnRegistPoints(agv.Name, registed_tag_except_current_tag, out string errMsg);
                return true;
            }
            catch (Exception ex)
            {
                LOG.Critical(ex);
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

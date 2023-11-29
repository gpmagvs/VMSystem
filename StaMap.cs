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

namespace VMSystem
{
    public class StaMap
    {
        public static Map Map { get; set; }
        private static string MapFile => AGVSConfigulator.SysConfigs.MapConfigs.MapFileFullName;
        internal static event EventHandler<int> OnTagUnregisted;
        public static Dictionary<int, clsPointRegistInfo> RegistDictionary = new Dictionary<int, clsPointRegistInfo>();

        public static void Download()
        {
            Map = MapManager.LoadMapFromFile();
            Console.WriteLine($"圖資載入完成:{Map.Name} ,Version:{Map.Note}");
        }
        internal static List<MapPoint> GetParkableStations()
        {
            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.ToList().FindAll(sta => sta.IsParking);
            return chargeableStations;
        }
        internal static List<MapPoint> GetChargeableStations()
        {

            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.Where(sta => sta.Enable && sta.IsChargeAble()).ToList();
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

        internal static MapPoint GetPointByName(string name)
        {
            var point = Map.Points.FirstOrDefault(pt => pt.Value.Name == name);
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
        internal static bool RegistPoint(string Name, IEnumerable<MapPoint> mapPoints, out string error_message)
        {
            error_message = string.Empty;
            foreach (var item in mapPoints)
            {
                if (!RegistPoint(Name, item, out error_message))
                    return false;
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
                LOG.TRACE($"{Name} Regist Tag {TagNumber}");
                return true;
            }
            if (RegistDictionary.ContainsKey(TagNumber))
            {
                string registerName = RegistDictionary[TagNumber].RegisterAGVName;
                bool _success = registerName == Name;
                if (_success)
                {
                    LOG.TRACE($"{Name} Regist Tag {TagNumber}");

                }
                else
                {
                    error_message = $"Tag {TagNumber} is registed by [{registerName}]";
                    LOG.TRACE($"{Name} Regist Tag {TagNumber} Fail:{error_message}");

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
                    LOG.TRACE($"{Name} Regist Tag {TagNumber} Fail");
                }
                return registSuccess;
            }
        }
        internal static bool UnRegistPointBySystem(MapPoint mapPoint, out string error_message)
        {
            return UnRegistPoint("System", mapPoint, out error_message, true);
        }


        internal static bool UnRegistPoint(string Name, int TagNumber, out string error_message, bool IsBySystem = false)
        {
            error_message = string.Empty;
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
                        LOG.TRACE($"{Name} UnRegist Tag {TagNumber}");

                        LOG.TRACE($"{RegistDictionary.ToJson()}");
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
                LOG.TRACE($"{Name} UnRegist Tag {TagNumber}");
                return true;
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


        internal static List<MapPoint> GetRegistedPointsOfPath(List<MapPoint> path_to_nav, string navigating_agv_name)
        {
            var registPoints = RegistDictionary.Where(kp => kp.Value.RegisterAGVName != navigating_agv_name).Select(kp => StaMap.GetPointByTagNumber(kp.Key));
            IEnumerable<MapPoint> commonItems = registPoints.Intersect(path_to_nav, new MapPointComparer());
            return commonItems.OrderBy(pt => path_to_nav.IndexOf(pt)).ToList();
        }

        internal static string GetStationNameByTag(int tag)
        {
            var point = Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == tag);
            return point == null ? tag + "" : point.Name;
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

        internal static bool UnRegistPointByName(string name, int[] exception_tags, out int[] failure_tag)
        {
            failure_tag = null;
            List<int> failTags = new List<int>();
            foreach (var item in RegistDictionary.Where(kp => kp.Value.RegisterAGVName == name & !exception_tags.Contains(kp.Key)))
            {
                int tag = item.Key;
                bool unSuccess = UnRegistPoint(name, tag, out string msg, name == "System");
                if (!unSuccess)
                {
                    failTags.Add(tag);
                }
            }
            failure_tag = failTags.ToArray();
            return failure_tag.Length == 0;
        }

        internal static bool GetPointRegisterName(int tagNumber, out string agvName)
        {
            agvName = string.Empty;
            if (!RegistDictionary.TryGetValue(tagNumber, out var _rinfo))
                return false;
            agvName = _rinfo.RegisterAGVName;
            return agvName != null;
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

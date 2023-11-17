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

namespace VMSystem
{
    public class StaMap
    {
        public static Map Map { get; set; }
        private static string MapFile => AGVSConfigulator.SysConfigs.MapConfigs.MapFileFullName;
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
        internal static bool RegistPoint(string Name, MapPoint mapPoint, out string error_message)
        {
            error_message = string.Empty; ;
            try
            {
                if (mapPoint == null)
                    return false;
                bool success = mapPoint.TryRegistPoint(Name, out var _info);
                if (mapPoint.RegistsPointIndexs.Length > 0)
                {
                    foreach (var item in mapPoint.RegistsPointIndexs)
                    {
                        if (StaMap.Map.Points.TryGetValue(item, out var pt))
                            pt.TryRegistPoint(Name, out _info);
                    }
                }
                return success;
            }
            catch (Exception ex)
            {
                error_message = ex.Message;
                return false;
            }
        }

        internal static bool UnRegistPoint(string Name, MapPoint mapPoint, out string error_message)
        {
            error_message = string.Empty;
            try
            {

                bool success = mapPoint.TryUnRegistPoint(Name, out var _info);
                PartsAGVSHelper.UnRegistStationRequestToAGVS(new List<string>() { mapPoint.Name });
                if (mapPoint.RegistsPointIndexs.Length > 0)
                {
                    foreach (var item in mapPoint.RegistsPointIndexs)
                    {
                        if (StaMap.Map.Points.TryGetValue(item, out var pt))
                            pt.TryUnRegistPoint(Name, out _info);
                    }
                }
                return success;
            }
            catch (Exception ex)
            {
                error_message = ex.Message;
                return false;
            }
        }

        internal static bool CheckTagExistOnMap(int currentTag)
        {
            return Map.Points.Select(pt => pt.Value.TagNumber).Contains(currentTag);
        }
        /// <summary>
        /// 取得除指令AGV名稱以外的所有註冊點位
        /// </summary>
        /// <param name="expect_agv_name"></param>
        /// <exception cref="NotImplementedException"></exception>
        internal static List<MapPoint> GetRegistedPoint(string expect_agv_name)
        {
            return Map.Points.Values.Where(pt => pt.RegistInfo != null).Where(pt => pt.RegistInfo.IsRegisted && pt.RegistInfo.RegisterAGVName != expect_agv_name).ToList();
        }

        internal static List<MapPoint> GetRegistedPointsOfPath(List<MapPoint> path_to_nav, string navigating_agv_name)
        {
            //若路徑上有點位被註冊=>移動至被註冊點之前一點
            List<MapPoint> registedPoints = StaMap.GetRegistedPoint(navigating_agv_name);

            IEnumerable<MapPoint> commonItems = registedPoints.Intersect(path_to_nav, new MapPointComparer());
            return commonItems.OrderBy(pt => path_to_nav.IndexOf(pt)).ToList();
        }

        internal static string GetStationNameByTag(int tag)
        {
            var point = Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == tag);
            return point == null ? tag + "" : point.Name;
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

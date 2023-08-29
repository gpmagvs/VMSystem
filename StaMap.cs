using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.MAP;
using Newtonsoft.Json;
using System.IO.Compression;
using AGVSystemCommonNet6.Configuration;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

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

            var chargeableStations = Map.Points.Values.ToList().FindAll(sta => sta.IsChargeAble());
            return chargeableStations;
        }

        internal static List<MapPoint> GetAvoidStations()
        {
            if (Map == null)
                Download();

            var avoidStations = Map.Points.Values.ToList().FindAll(sta => sta.IsAvoid);
            return avoidStations;
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
    }
}

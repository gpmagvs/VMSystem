using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpHelper;
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

        internal static void RegistPoint(string Name, MapPoint value)
        {
            if (value == null)
                return;
            value.TryRegistPoint(Name, out var _info);
            if (value.RegistsPointIndexs.Length > 0)
            {
                foreach (var item in value.RegistsPointIndexs)
                {
                    if (StaMap.Map.Points.TryGetValue(item, out var pt))
                        pt.TryRegistPoint(Name, out _info);
                }
            }
        }

        internal static void UnRegistPoint(string Name, MapPoint currentMapPoint)
        {
            currentMapPoint.TryUnRegistPoint(Name, out var _info);
            if (currentMapPoint.RegistsPointIndexs.Length > 0)
            {
                foreach (var item in currentMapPoint.RegistsPointIndexs)
                {
                    if (StaMap.Map.Points.TryGetValue(item, out var pt))
                        pt.TryUnRegistPoint(Name, out _info);
                }
            }
        }

        internal static bool CheckTagExistOnMap(int currentTag)
        {
            return Map.Points.Select(pt => pt.Value.TagNumber).Contains(currentTag);
        }
    }
}

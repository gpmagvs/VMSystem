using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.MAP;
using Newtonsoft.Json;
using System.IO.Compression;

namespace VMSystem
{
    public class StaMap
    {
        public static Map Map { get; set; }
        private static string MapFile = "C:\\AGVS\\Map\\Map_UMTC_5F_SMK_OVEN.json";

        public static void Download(string map_file = null)
        {
            MapFile = map_file == null ? MapFile : map_file;
            Map = MapManager.LoadMapFromFile(MapFile);
            Console.WriteLine($"圖資載入完成:{Map.Name} ,Version:{Map.Note}");
        }

        internal static List<MapStation> GetParkableStations()
        {
            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.ToList().FindAll(sta => sta.IsParking);
            return chargeableStations;
        }
        internal static List<MapStation> GetChargeableStations()
        {

            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.ToList().FindAll(sta => sta.IsChargeAble());
            return chargeableStations;
        }

        internal static bool TryGetPointByTagNumber(int tagNumber, out MapStation point)
        {
            point = Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == tagNumber);
            return point != null;
        }

    }
}

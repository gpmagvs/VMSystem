using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.MAP;
using Newtonsoft.Json;

namespace VMSystem
{
    public class StaMap
    {
        public static Map Map { get; set; }

        public static void Download()
        {

            Map = MapManager.LoadMapFromFile();
            Console.WriteLine($"圖資載入完成:{Map.Name} ,Version:{Map.Note}");
        }

        internal static List<MapStation> GetChargeableStations()
        {

            if (Map == null)
                Download();

            var chargeableStations = Map.Points.Values.ToList().FindAll(sta => sta.IsChargeAble());
            return chargeableStations;
        }
    }
}

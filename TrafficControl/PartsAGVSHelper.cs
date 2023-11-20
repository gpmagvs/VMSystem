using AGVSystemCommonNet6.Configuration;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AGVSystemCommonNet6.Microservices.VMS;
using static AGVSystemCommonNet6.Microservices.VMS.clsPartsAGVSRegionRegistService.RegistEventObject;

namespace VMSystem.TrafficControl
{
    internal class PartsAGVSHelper
    {
        public class PartAGVConnectParameters
        {
            public bool NeedRegistRequestToParts;
            public int Port = 433;
            public string PartServerIP = "127.0.0.1";
        }

        public static bool NeedRegistRequestToParts = false;
        public static int port = 433;
        public static string PartsServerIP = "127.0.0.1";

        public static void LoadParameters(string FilePath)
        {
            var Parameters = new PartAGVConnectParameters();
            if (File.Exists(FilePath))
            {
                Parameters = Newtonsoft.Json.JsonConvert.DeserializeObject<PartAGVConnectParameters>(File.ReadAllText(FilePath));
                NeedRegistRequestToParts = Parameters.NeedRegistRequestToParts;
                port = Parameters.Port;
                PartsServerIP = Parameters.PartServerIP;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            }
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(Parameters, Formatting.Indented));
        }

        public static async Task<bool> RegistStationRequestToAGVS(List<string> List_RegistNames, string AGVName = "AMCAGV")
        {
            if (!NeedRegistRequestToParts)
            {
                return true;
            }
            clsPartsAGVSRegionRegistService parts_service = new clsPartsAGVSRegionRegistService(PartsServerIP, port);
            var result = await parts_service.Regist(AGVName, List_RegistNames);
            return result.accept;
        }

        public static async Task<bool> UnRegistStationRequestToAGVS(List<string> List_UnRegistName, string AGVName = "AMCAGV")
        {
            return await await Task.Factory.StartNew(async () =>
            {

                if (!NeedRegistRequestToParts)
                {
                    return true;
                }
                clsPartsAGVSRegionRegistService parts_service = new clsPartsAGVSRegionRegistService(PartsServerIP, port);
                var result = await parts_service.Unregist(AGVName, List_UnRegistName);
                return result.accept;
            });
        }

        public static async Task<Dictionary<string, string>> QueryAGVSRegistedAreaName()
        {
            if (!NeedRegistRequestToParts)
            {
                return new Dictionary<string, string>();
            }
            clsPartsAGVSRegionRegistService parts_service = new clsPartsAGVSRegionRegistService(PartsServerIP, port);
            var result = await parts_service.Query();
            return result.Item2;
        }
    }
}

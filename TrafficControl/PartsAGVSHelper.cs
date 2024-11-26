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
using AGVSystemCommonNet6;
using NLog;

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
        static Logger Logger = LogManager.GetCurrentClassLogger();
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

        public static async Task<(bool confirm, string message, string responseJson)> RegistStationRequestToAGVS(List<string> List_RegistNames, string AGVName = "AMCAGV")
        {
            if (!NeedRegistRequestToParts)
            {
                return (true, "Setting as NO Need To Regist To PARTS", "");
            }
            clsPartsAGVSRegionRegistService parts_service = new clsPartsAGVSRegionRegistService(PartsServerIP, port);

            (bool accept, string message, string responseJson) result = (false, "", "");
            int retryNum = 0;
            while (!result.accept)
            {
                retryNum++;
                if (retryNum >= 5)
                {
                    Logger.Error($"Unregist Points to Parts System FAILURE...TIMEOUT");
                    return (false, "Unregist Points to Parts System FAILURE...TIMEOUT", "");
                }
                await Task.Delay(500);
                result = await parts_service.Regist(AGVName, List_RegistNames);
            }
            Logger.Info($"Regist Points to Parts System Result: {result.ToJson()}");
            return result;
        }

        /// <summary>
        /// 解除除了指定的站名以外的所有站點(會先查詢，再解註冊)
        /// </summary>
        /// <param name="ExceptStationNames"></param>
        /// <param name="AGVName"></param>
        /// <returns></returns>
        public static async Task<bool> UnRegistStationExceptSpeficStationName(List<string> ExceptStationNames, string AGVName = "AMCAGV")
        {
            try
            {
                using clsPartsAGVSRegionRegistService parts_service = new clsPartsAGVSRegionRegistService(PartsServerIP, port);
                var QueryResult = await parts_service.Query();
                var RegistedInfo = QueryResult.Item2;
                var toUnRegistNames = RegistedInfo.Where(keypair => keypair.Value == AGVName && !ExceptStationNames.Contains(keypair.Key)).Select(kp => kp.Key);
                Logger.Trace($"[UnRegistStationExceptSpeficStationName] To UnRegist Names :{toUnRegistNames.ToJson()}");
                if (toUnRegistNames.Count() == 0)
                {
                    return true;
                }
                var result = await parts_service.Unregist(AGVName, toUnRegistNames.ToList());
                var success = result.accept;
                Logger.Info($"UnRegist Region Expect {string.Join(",", ExceptStationNames)}, Success?..{success}");
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error($"{ex.Message}");

                return false;
            }
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
                (bool accept, string message, string responseJson) result = (false, "", "");
                int retryNum = 0;

                while (!result.accept)
                {
                    retryNum++;
                    if (retryNum >= 5)
                    {
                        Logger.Fatal($"Unregist Points to Parts System FAILURE...TIMEOUT");
                        return false;
                    }
                    await Task.Delay(500);
                    result = await parts_service.Unregist(AGVName, List_UnRegistName);
                }
                Logger.Info($"Unregist Points to Parts System Result: {result.ToJson()}");
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

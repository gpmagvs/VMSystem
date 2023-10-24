using AGVSystemCommonNet6.Configuration;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

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

        public static bool RegistStationRequestToAGVS(List<string> List_RegistNames, string AGVName = "AMCAGV")
        {
            if (!NeedRegistRequestToParts)
            {
                return true;
            }
            return TrafficRegistEvent("Regist", List_RegistNames, AGVName) == "OK";
        }

        public static bool UnRegistStationRequestToAGVS(List<string> List_UnRegistName, string AGVName = "AMCAGV")
        {
            if (!NeedRegistRequestToParts)
            {
                return true;
            }
            return TrafficRegistEvent("Unregist", List_UnRegistName, AGVName) == "OK";
        }

        public static Dictionary<string, string> QueryAGVSRegistedAreaName()
        {
            if (!NeedRegistRequestToParts)
            {
                return new Dictionary<string, string>();
            }
            string QueryResult =  TrafficRegistEvent("Query", new List<string>(), "");
            if (QueryResult == "NG")
            {
                return new Dictionary<string, string>();
            }
            Dictionary<string, string> OutputData = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(QueryResult);
            if (OutputData ==null)
            {
                return new Dictionary<string, string>();
            }
            return OutputData;
        }

        private static string TrafficRegistEvent(string EventString , List<string> List_StationNames, string AGVName = "AMCAGV")
        {
            RegistEventObject SendObject = new RegistEventObject()
            {
                AGVName = AGVName,
                List_AreaName = List_StationNames,
                RegistEvent = EventString
            };
            string SendOutMessage = Newtonsoft.Json.JsonConvert.SerializeObject(SendObject);

            Socket ClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                ClientSocket.Connect(PartsServerIP, port);
                ClientSocket.Send(Encoding.ASCII.GetBytes(SendOutMessage));
            }
            catch (Exception exp)
            {
                //無法連線
                return "NG";
            }
            int retryCount = 0;
            while (true)
            {
                retryCount += 1;
                if (ClientSocket.Available == 0)
                {
                    if (retryCount >100)
                    {
                        return "NG";
                    }
                    Thread.Sleep(100);
                }
                else
                {
                    Thread.Sleep(100);
                    break;
                }
            }
            byte[] ReceiveData = new byte[ClientSocket.Available];
            ClientSocket.Receive(ReceiveData);
            string ReceiveDataString = System.Text.Encoding.ASCII.GetString(ReceiveData);
            return ReceiveDataString ;
        }

        private class RegistEventObject
        {
            public string AGVName = "External";
            public List<string> List_AreaName = new List<string>();
            public string RegistEvent = "Regist";
        }
    }
}

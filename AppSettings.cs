using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VMSystem.AGV;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem
{
    public class AppSettings
    {
        //private static string appsettingsFile => Debugger.IsAttached ? "appsettings.Development.json" : "appsettings.json";
        private static string appsettingsFile => "appsettings.json";
        private static IConfiguration _config
        {
            get
            {
                ConfigurationBuilder builder = new ConfigurationBuilder();
                builder.SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile(appsettingsFile);
                return builder.Build();
            }
        }

        public static Dictionary<string, object> GetAppsettings()
        {
            string settingsJosnFile = Path.Combine(Environment.CurrentDirectory , appsettingsFile);

            string json = File.ReadAllText(settingsJosnFile);
            Dictionary<string, object>? con = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            return con;
        }

        public static Dictionary<VMS_GROUP, VMSConfig> VMSConfigs
        {
            get
            {
                var json = GetAppsettings()["VMS"].ToString();
                return JsonConvert.DeserializeObject<Dictionary<VMS_GROUP, VMSConfig>>(json);
            }
        }


        public class VMSConfig
        {
            public Dictionary<string, clsAGVOptions> AGV_List { get; set; }

        }
    }
}

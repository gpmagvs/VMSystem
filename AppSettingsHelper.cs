using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VMSystem
{
    public class AppSettingsHelper
    {
        private static IConfiguration configuration
        {
            get
            {
                try
                {

                    string settingsJosnFile = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                    var configBuilder = new ConfigurationBuilder()
                        .SetBasePath(AppContext.BaseDirectory)
                        .AddJsonFile(settingsJosnFile);
                    var configuration = configBuilder.Build();
                    return configuration;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }



        public AppSettingsHelper()
        {

        }

        public static Dictionary<string, object> GetAppsettings()
        {
            string settingsJosnFile = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

            string json = File.ReadAllText(settingsJosnFile);
            Dictionary<string, object>? con = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            return con;
        }

        public static T GetValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string key)
        {
            return configuration.GetValue(key, default(T));
        }
    }
}



using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Log;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem
{
    public static partial class Startup
    {
        public static void Fuck()
        {
            Console.WriteLine("Start!");
        }

        internal static void ConfigurationInit()
        {
            AGVSConfigulator.Init();
            PartsAGVSHelper.LoadParameters("C:\\AGVS\\PartConnection.json");
        }

        internal static void ConsoleInit()
        {
            Console.Title = "GPM-車輛管理系統(VMS)";
        }

        internal static void DBInit(WebApplicationBuilder builder, WebApplication app)
        {
            try
            {
                using (IServiceScope scope = app.Services.CreateScope())
                {
                    using (AGVSDbContext dbContext = scope.ServiceProvider.GetRequiredService<AGVSDbContext>())
                    {
                        dbContext.Database.EnsureCreated();
                        dbContext.SaveChanges();
                    }
                }

                AGVSDatabase.Initialize().GetAwaiter().GetResult();
                TaskDatabaseHelper dbheper = new TaskDatabaseHelper();
                dbheper.SetRunningTaskWait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"資料庫初始化異常-請確認資料庫! {ex.Message}");
                Environment.Exit(4);
            }
        }

        internal static void LOGInstanceInit()
        {

            LOG.SetLogFolderName("VMS LOG");
            LOG.INFO("VMS System Start");
        }

        internal static void StaticFileInit(WebApplication app)
        {
            var AGVUpdateFileFolder = AGVSystemCommonNet6.Configuration.AGVSConfigulator.SysConfigs.AGVUpdateFileFolder;
            Directory.CreateDirectory(AGVUpdateFileFolder);
            var fileProvider = new PhysicalFileProvider(AGVUpdateFileFolder);
            var requestPath = "/AGVUpdateFiles";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = requestPath,
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream", 
            });

            app.UseDirectoryBrowser(new DirectoryBrowserOptions
            {
                FileProvider = fileProvider,
                RequestPath = requestPath
            });

        }

        internal static void VMSInit()
        {
            StaMap.Download();
            VMSManager.Initialize().ContinueWith(tk=> {
                Dispatch.DispatchCenter.Initialize();
            });
            TrafficControlCenter.Initialize();
        }
    }
}

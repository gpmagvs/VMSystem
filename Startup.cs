

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

        internal static async void DBInit(WebApplicationBuilder builder, WebApplication app)
        {
            try
            {
                //AGVSDatabase.Initialize().GetAwaiter().GetResult();

                using AGVSDatabase database = new AGVSDatabase();
                var queingTasks = database.tables.Tasks.Where(task => task.State == AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.NAVIGATING ||
                                                  task.State == AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.WAIT).ToList();
                foreach (var _task in queingTasks)
                {
                    _task.State = AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.FAILURE;
                    _task.FailureReason = "系統重啟刪除任務";
                    _task.FinishTime = System.DateTime.Now;
                }
                await database.SaveChanges();
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
            VMSManager.Initialize().ContinueWith(tk =>
            {
                Dispatch.DispatchCenter.Initialize();
            });
            TrafficControlCenter.Initialize();
        }
    }
}

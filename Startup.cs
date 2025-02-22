﻿

using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Notify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using NLog;
using VMSystem.Dispatch.Regions;
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

        internal static void ConfigurationInit(Logger? logger = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            string testAppsettingJsonFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "appsettings.Test.json");
            builder.Configuration.AddJsonFile(testAppsettingJsonFilePath, optional: true, true);//嘗試注入測試用  

            string configRootFolder = builder.Configuration.GetValue<string>("AGVSConfigFolder");
            configRootFolder = string.IsNullOrEmpty(configRootFolder) ? @"C:\AGVS" : configRootFolder;
            logger?.Debug($"派車系統參數檔資料夾路徑={configRootFolder}");

            AGVSConfigulator.Init(configRootFolder);
            PartsAGVSHelper.LoadParameters("C:\\AGVS\\PartConnection.json");
        }

        internal static void ConsoleInit()
        {
            var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Console.Title = $"GPM-車輛管理系統(VMS)-v.{appVersion}";
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
        internal static void StaticFileInit(WebApplication app)
        {

        }

        internal static void VMSInit()
        {
            StaMap.Download();
            TrafficControlCenter.Initialize();
            RegionManager.Initialze();
            NotifyServiceHelper.OnMessage += NotifyServiceHelper_OnMessage;
        }

        private static void NotifyServiceHelper_OnMessage(object? sender, NotifyServiceHelper.NotifyMessage notifyMessage)
        {
            Logger _logger = LogManager.GetLogger("NotifierLog");
            Task.Run(() =>
            {
                string msg = notifyMessage.message;
                switch (notifyMessage.type)
                {
                    case NotifyServiceHelper.NotifyMessage.NOTIFY_TYPE.info:
                        _logger.Info(msg);
                        break;
                    case NotifyServiceHelper.NotifyMessage.NOTIFY_TYPE.warning:
                        _logger.Warn(msg);
                        break;
                    case NotifyServiceHelper.NotifyMessage.NOTIFY_TYPE.error:
                        _logger.Error(msg);
                        break;
                    case NotifyServiceHelper.NotifyMessage.NOTIFY_TYPE.success:
                        _logger.Info(msg);
                        break;
                    default:
                        break;
                }

                //
            });
        }
    }
}

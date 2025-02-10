using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.BackgroundServices;
using AGVSystemCommonNet6.PartsModels;
using AGVSystemCommonNet6.Sys;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;
using KGSWebAGVSystemAPI.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Web;
using VMSystem;
using VMSystem.BackgroundServices;
using VMSystem.Dispatch;
using VMSystem.Services;

if (ProcessTools.IsProcessRunning("VMSystem", out List<int> pids))
{
    Console.WriteLine($"VMS Program is already running({string.Join(",", pids)})");
    Console.WriteLine("Press any key to exit..."); Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
}

Startup.ConfigurationInit();
AlarmManagerCenter.InitializeAsync().GetAwaiter().GetResult();
AlarmManager.LoadVCSTrobleShootings();
EnvironmentVariables.AddUserVariable("VMSInstall", Environment.CurrentDirectory);
var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
logger.Info("VMSystem Program Start");
try
{
    SECSConfigsService _secsConfigsService = new SECSConfigsService(Path.Combine(AGVSConfigulator.SysConfigs.CONFIGS_ROOT_FOLDER, "SECSConfigs"));
    _secsConfigsService.InitializeAsync();

    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
    builder.Host.UseNLog();
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
        {
            policy.AllowAnyMethod()
                  .AllowAnyHeader()
                  .SetIsOriginAllowed(origin => true) // 允许任何来源
                  .AllowCredentials(); // 允许凭据
        });
    });
    builder.Services.AddWebSockets(options =>
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(600);
    });
    builder.Services.Configure<JsonOptions>(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = null;
        options.SerializerOptions.PropertyNameCaseInsensitive = false;
        options.SerializerOptions.WriteIndented = true;
    });

    string DBConnection = AGVSConfigulator.SysConfigs.DBConnection;
    builder.Services.AddDbContext<AGVSDbContext>(options =>
    {
        options.UseSqlServer(DBConnection);
    });


    builder.Services.AddScoped<VehicleOnlineRequestByAGVService>();
    builder.Services.AddScoped<VehicleOnlineBySystemService>();
    builder.Services.AddScoped<VehicleMaintainService>();
    builder.Services.AddHostedService<DatabaseBackgroundService>();
    builder.Services.AddHostedService<VehicleStateService>();
    builder.Services.AddHostedService<FrontEndDataCollectionBackgroundService>();
    builder.Services.AddHostedService<EquipmentScopeBackgroundService>();
    builder.Services.AddHostedService<TaskPathConflicDetectionService>();
    builder.Services.AddHostedService<OrderStateMonitorBackgroundService>();
    builder.Services.AddHostedService<PCPerformanceService>();
    //builder.Services.AddHostedService<VehicleStatusDownSoundAlarmBackgroundService>();
    builder.Services.AddHostedService<AGVStatsuCollectBackgroundService>();
    builder.Services.AddHostedService<TrafficScopeService>();
    builder.Services.AddHostedService<VMSManageHostService>();
    builder.Services.AddScoped<SECSConfigsService>(service => _secsConfigsService);

    var optimizedVehicleDispatcher = new clsOptimizeAGVDispatcher(AGVSConfigulator.ConfigsFilesFolder);
    builder.Services.AddSingleton<clsOptimizeAGVDispatcher>(optimizedVehicleDispatcher);
    builder.Services.AddHostedService(sp => optimizedVehicleDispatcher);

    if (AGVSConfigulator.SysConfigs.LinkPartsAGVSystem)
    {
        builder.Services.AddDbContext<PartsAGVS_InfoContext>(options =>
        {
            options.UseSqlServer(AGVSConfigulator.SysConfigs.PartsAGVSDBConnection);
        });
        builder.Services.AddHostedService<PartsAGVInfoService>();
    }

    if (AGVSConfigulator.SysConfigs.BaseOnKGSWebAGVSystem)
    {
        builder.Services.AddDbContext<WebAGVSystemContext>(options =>
        {
            options.UseSqlServer(AGVSConfigulator.SysConfigs.KGSWebAGVSystemDBConnection);
        });
        builder.Services.AddHostedService<KGSVehicleStatusSyncBackgroundService>();
    }

    //add signalIR service
    builder.Services.AddSignalR().AddJsonProtocol(options => { options.PayloadSerializerOptions.PropertyNamingPolicy = null; });

    var app = builder.Build();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        logger.Error($"Ctrl+c triggered 應用程式正在關閉中...");
    });
    lifetime.ApplicationStopped.Register(() =>
    {
        logger.Error($"Ctrl+c triggered 應用程式已關閉");
    });

    AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
    {
        logger.Error($"Close action botton triggered.  應用程式正在關閉中...");
    };
    try
    {
        Startup.ConsoleInit();
        Startup.DBInit(builder, app);
        Startup.VMSInit();
        Startup.StaticFileInit(app);
    }
    catch (Exception ex)
    {
        Thread.Sleep(3000);
        Environment.Exit(1);
    }
    app.UseMiddleware<ApiLoggingMiddleware>();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseDefaultFiles(new DefaultFilesOptions());
    app.UseStaticFiles();
    app.UseRouting();
    //app.UseCors(c => c.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
    app.UseCors("AllowAll");
    app.UseWebSockets();
    //app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHub<FrontEndDataHub>("/FrontEndDataHub");
    app.Run();

}
catch (Exception ex)
{
    logger.Error(ex);
}
finally
{
    LogManager.Shutdown();
}
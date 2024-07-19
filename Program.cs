using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.BackgroundServices;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Sys;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Web;
using VMSystem;
using VMSystem.BackgroundServices;
using VMSystem.Services;

Startup.ConfigurationInit();
EnvironmentVariables.AddUserVariable("VMSInstall", Environment.CurrentDirectory);
var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
logger.Info("VMSystem Program Start");
try
{
    var builder = WebApplication.CreateBuilder(args);
    //將NLog註冊到此專案內
    builder.Logging.ClearProviders();
    //設定log紀錄的最小等級
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

    //add signalIR service
    builder.Services.AddSignalR().AddJsonProtocol(options => { options.PayloadSerializerOptions.PropertyNamingPolicy = null; });

    var app = builder.Build();

    try
    {
        Startup.ConsoleInit();
        Startup.DBInit(builder, app);
        Startup.LOGInstanceInit();
        Startup.VMSInit();
        Startup.StaticFileInit(app);
    }
    catch (Exception ex)
    {
        LOG.Critical(ex);
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
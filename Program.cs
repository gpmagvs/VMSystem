using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.BackgroundServices;
using AGVSystemCommonNet6.Log;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Serilog.Filters;
using VMSystem;
using VMSystem.BackgroundServices;
using VMSystem.Controllers;
using VMSystem.Services;


Startup.ConfigurationInit();

var builder = WebApplication.CreateBuilder(args);

string logRootFolder = AGVSConfigulator.SysConfigs.LogFolder;
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    //全部的LOG但不包含EF Core Log
    .WriteTo.Logger(lc => lc
                .WriteTo.Console()
                .Filter.ByExcluding(Matching.FromSource("Microsoft.EntityFrameworkCore")) // 過濾EF Core Log  
                .WriteTo.File(
                    path: $"{logRootFolder}/VMSystem/log-.log", // 路徑
                    rollingInterval: RollingInterval.Day, // 每小時一個檔案
                    retainedFileCountLimit: 24 * 90,// 最多保留 30 天份的 Log 檔案
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    rollOnFileSizeLimit: false,
                    fileSizeLimitBytes: null
                    ))
////只有 AGVSystem.ApiLoggingMiddleware 
//.WriteTo.Logger(lc => lc
//                .WriteTo.Console()
//                .Filter.ByIncludingOnly(Matching.FromSource("AGVSystem.ApiLoggingMiddleware"))
//                .WriteTo.File(
//                    path: $"{logRootFolder}/AGVS/api/log-.log",
//                    rollingInterval: RollingInterval.Day,
//                    retainedFileCountLimit: 24 * 90,
//                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
//                    rollOnFileSizeLimit: false,
//                    fileSizeLimitBytes: null
//                )
//            )
);

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

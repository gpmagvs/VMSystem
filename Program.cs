using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.BackgroundServices;
using AGVSystemCommonNet6.Log;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using VMSystem;
using VMSystem.BackgroundServices;
using VMSystem.Controllers;
using VMSystem.Services;


Startup.ConfigurationInit();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
builder.Services.AddHostedService<DatabaseBackgroundService>();
builder.Services.AddHostedService<VehicleStateService>();

builder.Services.AddScoped<VehicleOnlineRequestByAGVService>();
builder.Services.AddScoped<VehicleOnlineBySystemService>();

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

var app = builder.Build();

try
{
    Startup.ConsoleInit();
    Startup.DBInit(builder, app);
    Startup.LOGInstanceInit();
    Startup.VMSInit();
    Startup.StaticFileInit(app);
    WebsocketClientMiddleware.middleware.Initialize();

}
catch (Exception ex)
{
    LOG.Critical(ex);
    Thread.Sleep(3000);
    Environment.Exit(1);
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(1) });
app.UseCors(c => c.AllowAnyHeader().AllowAnyOrigin().AllowAnyMethod());
app.UseAuthorization();
app.MapControllers();

app.Run();

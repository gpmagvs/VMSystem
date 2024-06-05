using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.BackgroundServices;
using AGVSystemCommonNet6.Log;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebSockets;
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
builder.Services.AddSignalR().AddJsonProtocol(options => { options.PayloadSerializerOptions.PropertyNamingPolicy = null; });
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

//add signalIR service
builder.Services.AddSignalR();

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

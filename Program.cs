
using Microsoft.AspNetCore.Http.Json;
using VMSystem;
using VMSystem.VMS;
using Microsoft.EntityFrameworkCore;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.User;
using Microsoft.Data.Sqlite;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.DATABASE.Helpers;
using VMSystem.Controllers;
using Microsoft.Extensions.FileProviders;
Console.Title = "GPM-車輛管理系統(VMS)";
LOG.SetLogFolderName("VMS LOG");
LOG.INFO("VMS System Start");
AGVSConfigulator.Init();
WebsocketClientMiddleware.middleware.Initialize();
PartsAGVSHelper.LoadParameters("C:\\AGVS\\PartConnection.json");
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);


string DBConnection = AGVSConfigulator.SysConfigs.DBConnection;
//Directory.CreateDirectory(Path.GetDirectoryName(DBConnection.Split('=')[1]));
//var connectionString = new SqliteConnectionStringBuilder(DBConnection)
//{
//    Mode = SqliteOpenMode.ReadWriteCreate,
//}.ToString();

builder.Services.AddDbContext<AGVSDbContext>(options => options.UseSqlServer(DBConnection));

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.WriteIndented = true;
});
var app = builder.Build();


using (IServiceScope scope = app.Services.CreateScope())
{
    using (AGVSDbContext dbContext = scope.ServiceProvider.GetRequiredService<AGVSDbContext>())
    {
        dbContext.Database.EnsureCreated();
        dbContext.SaveChanges();
    }
}


// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
app.UseSwagger();
app.UseSwaggerUI();
//}

app.UseWebSockets();

app.UseCors(c => c.AllowAnyHeader().AllowAnyOrigin().AllowAnyMethod());

app.UseAuthorization();

app.MapControllers();

var AGVUpdateFileFolder = AGVSystemCommonNet6.Configuration.AGVSConfigulator.SysConfigs.AGVUpdateFileFolder;
Directory.CreateDirectory(AGVUpdateFileFolder);
var fileProvider = new PhysicalFileProvider(AGVUpdateFileFolder);
var requestPath = "/AGVUpdateFiles";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = fileProvider,
    RequestPath = requestPath,
    ServeUnknownFileTypes = true,  // 允许服务未知文件类型
    DefaultContentType = "application/octet-stream",  // 为未知文件类型设置默认 MIME 类型
});

app.UseDirectoryBrowser(new DirectoryBrowserOptions
{
    FileProvider = fileProvider,
    RequestPath = requestPath
});

try
{
    TaskDatabaseHelper dbheper = new TaskDatabaseHelper();
    dbheper.SetRunningTaskWait();
    StaMap.Download();
    VMSManager.Initialize(builder.Configuration);
    TrafficControlCenter.Initialize();

}
catch (Exception ex)
{
    LOG.Critical(ex);
    Thread.Sleep(3000);
    Environment.Exit(1);
}

app.Run();

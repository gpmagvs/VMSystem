
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

LOG.SetLogFolderName("VMS LOG");
LOG.INFO("VMS System Start");
AGVSConfigulator.Init();
PartsAGVSHelper.LoadParameters("C:\\AGVS\\PartConnection.json");
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);


string DBConnection = AGVSConfigulator.SysConfigs.DBConnection;
Directory.CreateDirectory(Path.GetDirectoryName(DBConnection.Split('=')[1]));
var connectionString = new SqliteConnectionStringBuilder(DBConnection)
{
    Mode = SqliteOpenMode.ReadWriteCreate,
}.ToString();

builder.Services.AddDbContext<AGVSDbContext>(options => options.UseSqlite(connectionString));

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
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets();

app.UseCors(c => c.AllowAnyHeader().AllowAnyOrigin().AllowAnyMethod());

app.UseAuthorization();

app.MapControllers();



StaMap.Download();
VMSManager.Initialize(builder.Configuration);
TrafficControlCenter.Initialize();

TaskDatabaseHelper dbheper = new TaskDatabaseHelper();
dbheper.SetRunningTaskWait();

WebsocketClientMiddleware.StartViewDataCollect();

app.Run();

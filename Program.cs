
using Microsoft.AspNetCore.Http.Json;
using VMSystem;
using VMSystem.VMS;
using Microsoft.EntityFrameworkCore;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Configuration;

LOG.SetLogFolderName("VMS LOG");
LOG.INFO("VMS System Start");
AGVSConfigulator.Init();
var builder = WebApplication.CreateBuilder(args);
string DBConnection = builder.Configuration.GetConnectionString("DefaultConnection");
Directory.CreateDirectory(Path.GetDirectoryName(DBConnection.Split('=')[1]));

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
builder.Services.AddDbContext<AGVSDbContext>(options => options.UseSqlite(DBConnection));

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.WriteIndented = true;
});
var app = builder.Build();


using (var scope = app.Services.CreateScope())
{
    AGVSDbContext TaskDbContext = scope.ServiceProvider.GetRequiredService<AGVSDbContext>();
    TaskDbContext.Database.EnsureCreated();
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



clsAGVTaskDisaptchModule.OnAGVSOnlineModeChangedRequest += TrafficControlCenter.TrafficControlCheck;

StaMap.Download();
VMSManager.Initialize(builder.Configuration);

app.Run();


using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment.AGV;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.BackgroundServices
{
    public class AGVStatsuCollectBackgroundService : BackgroundService
    {
        IServiceScopeFactory _scopeFactory;
        public AGVStatsuCollectBackgroundService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 配置 AutoMapper
            var config = new MapperConfiguration(cfg => cfg.CreateMap<AGVStatus, AGVStatus>());
            var mapper = config.CreateMapper();

            await Task.Delay(1000).ContinueWith(async tk =>
            {

                while (true)
                {
                    try
                    {

                        await Task.Delay(1000);
                        using AGVSDbContext dbContext = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<AGVSDbContext>();
                        foreach (IAGV vehicle in VMSManager.AllAGV)
                        {
                            AGVStatus agvStatus = CreateAGVStatus(vehicle);
                            AGVStatus agvStatusInDB = dbContext.EQStatus_AGV.FirstOrDefault(s => s.Name == agvStatus.Name);
                            if (agvStatusInDB == null)
                            {
                                dbContext.EQStatus_AGV.Add(agvStatus);
                                continue;
                            }
                            else
                            {
                                mapper.Map(agvStatus, agvStatusInDB);
                                dbContext.Entry(agvStatusInDB).State = EntityState.Modified;
                            }

                        }

                        int num = await dbContext.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {

                    }
                }
            });
        }

        private AGVStatus CreateAGVStatus(IAGV _vehicle)
        {
            var status = new AGVStatus();
            status.Name = _vehicle.Name;
            status.Tag = _vehicle.currentMapPoint.TagNumber;
            status.Connected = _vehicle.connected;
            status.BatLevel = _vehicle.states.Electric_Volume.First();
            status.BatDisChargeCurrent = 122000;

            status.CoordinateX = _vehicle.states.Coordination.X;
            status.CoordinateY = _vehicle.states.Coordination.Y;

            status.CurrentPathTag = string.Join("-", _vehicle.NavigationState.NextNavigtionPoints.Select(pt => pt.TagNumber));

            return status;
        }
    }
}

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
        AGVSDbContext dbContext;
        public AGVStatsuCollectBackgroundService(IServiceScopeFactory scopeFactory)
        {
            this.dbContext = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<AGVSDbContext>();
            //dbContext;
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

                        await dbContext.SaveChangesAsync();
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
            return status;
        }
    }
}
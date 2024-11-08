
using AGVSystemCommonNet6.DATABASE;

namespace VMSystem.Services
{
    public class TrafficScopeService : IHostedService
    {
        private AGVSDbContext dbContext;
        public TrafficScopeService(IServiceScopeFactory factory)
        {
            dbContext = factory.CreateScope().ServiceProvider.GetRequiredService<AGVSDbContext>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            TrafficControl.TrafficControlCenter.AGVDbContext = dbContext;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}

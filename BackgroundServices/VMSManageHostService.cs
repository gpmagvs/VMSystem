
using AGVSystemCommonNet6.DATABASE;
using VMSystem.VMS;

namespace VMSystem.BackgroundServices
{
    public class VMSManageHostService : IHostedService
    {
        AGVSDbContext dbContext;
        public VMSManageHostService(IServiceScopeFactory scopeFactory)
        {
            dbContext = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<AGVSDbContext>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            VMSManager.AGVSDbContext = dbContext;
            _ = VMSManager.Initialize().ContinueWith(tk =>
            {
                Dispatch.DispatchCenter.Initialize();
            });
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }


    }
}

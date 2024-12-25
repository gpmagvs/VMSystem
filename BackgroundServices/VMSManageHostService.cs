
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.VMS;

namespace VMSystem.BackgroundServices
{
    public class VMSManageHostService : IHostedService
    {
        IServiceProvider serviceProvider;
        AGVSDbContext dbContext;
        SECSConfigsService secsConfigService;
        public VMSManageHostService(IServiceScopeFactory scopeFactory)
        {
            serviceProvider = scopeFactory.CreateScope().ServiceProvider;
            dbContext = serviceProvider.GetRequiredService<AGVSDbContext>();
            secsConfigService = serviceProvider.GetRequiredService<SECSConfigsService>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            VMSManager.AGVSDbContext = dbContext;
            _ = VMSManager.Initialize().ContinueWith(tk =>
            {
                Dispatch.DispatchCenter.Initialize();
            });

            OrderHandlerBase.OnOrderStart += OrderHandlerBase_OnOrderStart;

        }

        private void OrderHandlerBase_OnOrderStart(object? sender, OrderHandlerBase.OrderStartEvnetArgs e)
        {
            secsConfigService.Reload();
            e.secsConfigsService = this.secsConfigService;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}

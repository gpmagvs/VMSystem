
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
            SECSConfigsService.logger.Trace("Initialize Service configuration when OrderHandlerBase_OnOrderStart event invoking");
            ManualResetEventSlim manualResetEventSlim = new ManualResetEventSlim(false);
            Task.Factory.StartNew(async () =>
            {
                await secsConfigService.InitializeAsync();
                manualResetEventSlim.Set();
            });
            bool inTime = manualResetEventSlim.Wait(TimeSpan.FromSeconds(1));
            e.isSecsConfigServiceInitialized = inTime;

            if (!inTime)
                SECSConfigsService.logger.Warn("Wait SECSConfigsService InitializeAsync done Timeout!");
            else
                SECSConfigsService.logger.Trace("SECSConfigsService InitializeAsync done");

            e.secsConfigsService = this.secsConfigService;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}

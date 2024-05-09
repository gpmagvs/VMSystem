
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.AGV;
using AGVSystemCommonNet6.AGVDispatch;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.BackgroundServices
{
    public class VehicleStateService : IHostedService, IDisposable
    {
        public static Dictionary<string, clsAGVStateDto> AGVStatueDtoStored => DatabaseCaches.Vehicle.VehicleStates.ToDictionary(v => v.AGV_Name, v => v);
        AGVSDbContext context;
        public VehicleStateService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
            context = _scopeFactory.CreateAsyncScope().ServiceProvider.GetRequiredService<AGVSDbContext>();

        }
        Timer _timer;
        private readonly IServiceScopeFactory _scopeFactory;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(() => DoWork());
        }

        private async Task DoWork()
        {
            while (true)
            {
                await Task.Delay(150);
                clsAGVStateDto CreateDTO(IAGV agv)
                {
                    GetStationsName(agv, out string currentLocDisplay, out string sourceDisplay, out string destineDisplay);
                    var dto = new clsAGVStateDto
                    {
                        AGV_Name = agv.Name,
                        Enabled = agv.options.Enabled,
                        BatteryLevel_1 = agv.states.Electric_Volume.Length >= 1 ? agv.states.Electric_Volume[0] : -1,
                        BatteryLevel_2 = agv.states.Electric_Volume.Length >= 2 ? agv.states.Electric_Volume[1] : -1,
                        OnlineStatus = agv.online_state,
                        MainStatus = agv.states.AGV_Status,
                        CurrentCarrierID = agv.states.CSTID.Length == 0 ? "" : agv.states.CSTID[0],
                        CurrentLocation = agv.states.Last_Visited_Node.ToString(),
                        Theta = agv.states.Coordination.Theta,
                        Connected = agv.connected,
                        Group = agv.VMSGroup,
                        Model = agv.model,
                        TaskName = agv.main_state == MAIN_STATUS.RUN ? agv.taskDispatchModule.OrderHandler.OrderData.TaskName : "",
                        //TaskRunStatus = agv.taskDispatchModule.TaskStatusTracker.TaskRunningStatus,
                        TaskRunAction = agv.taskDispatchModule.OrderHandler.OrderData.Action,
                        CurrentAction = agv.taskDispatchModule.OrderHandler.RunningTask.ActionType,
                        TransferProcess = agv.taskDispatchModule.OrderHandler.RunningTask.Stage,
                        TaskETA = agv.taskDispatchModule.TaskStatusTracker.NextDestineETA,
                        IsCharging = agv.states.IsCharging,
                        IsExecutingOrder = agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING,
                        VehicleWidth = agv.options.VehicleWidth,
                        VehicleLength = agv.options.VehicleLength,
                        Protocol = agv.options.Protocol,
                        IP = agv.options.HostIP,
                        Port = agv.options.HostPort,
                        Simulation = agv.options.Simulation,
                        LowBatLvThreshold = agv.options.BatteryOptions.LowLevel,
                        MiddleBatLvThreshold = agv.options.BatteryOptions.MiddleLevel,
                        HighBatLvThreshold = agv.options.BatteryOptions.HightLevel,
                        TaskSourceStationName = sourceDisplay,
                        TaskDestineStationName = destineDisplay,
                        StationName = currentLocDisplay
                    };
                    return dto;
                };
                bool haschanaged = false;
                foreach (var agv in VMSManager.AllAGV)
                {
                    var entity = CreateDTO(agv);
                    var statesCache = DatabaseCaches.Vehicle.VehicleStates.FirstOrDefault(v => v.AGV_Name == entity.AGV_Name);
                    if (statesCache == null)
                        continue;
                    if (statesCache.HasChanged(entity))
                    {
                        context.AgvStates.First(v => v.AGV_Name == entity.AGV_Name).Update(entity);
                        haschanaged = true;

                    }
                }
                if (haschanaged)
                {
                    await context.SaveChangesAsync();
                }
                void GetStationsName(IAGV agv, out string current, out string from, out string to)
                {
                    current = from = to = "";
                    current = agv.currentMapPoint.Graph.Display;
                    bool isExecuting = agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
                    clsTaskDto currentOrder = agv.CurrentRunningTask().OrderData;
                    if (currentOrder == null)
                        return;
                    if (!isExecuting)
                        return;
                    from = StaMap.GetStationNameByTag(currentOrder.From_Station_Tag);
                    to = StaMap.GetStationNameByTag(currentOrder.To_Station_Tag);
                }

            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

    }
}

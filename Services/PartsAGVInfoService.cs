
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using VMSystem.VMS;

namespace VMSystem.Services
{
    public class PartsAGVInfoService : IHostedService
    {
        public static Dictionary<string, clsAGVStateDto> PartsAGVStatueDtoStored = new Dictionary<string, clsAGVStateDto>();

        IServiceScopeFactory scopeFactory;
        public PartsAGVInfoService(IServiceScopeFactory scopeFactory)
        {
            this.scopeFactory = scopeFactory;
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(() => DoWork());
        }

        private async Task DoWork()
        {
            while (true)
            {
                try
                {

                    using (var scope = scopeFactory.CreateAsyncScope())
                    {
                        using (var context = scope.ServiceProvider.GetRequiredService<AGVSystemCommonNet6.PartsModels.PartsAGVS_InfoContext>())
                        {

                            var agvInfos = context.Agvinfos.AsNoTracking().ToList();
                            Console.WriteLine(agvInfos.ToJson());
                            foreach (var item in agvInfos)
                            {
                                var _agvName = "Parts_" + item.Agvname.TrimEnd(new char[1] { ' ' });
                                var _location = item.CurrentTagNumber.TrimEnd(new char[1] { ' ' });
                                var _taskName = item.DoTaskName.TrimEnd(new char[1] { ' ' });
                                var battery = item.Agvbattery;
                                var cstID = item.Stage1Cstid;
                                clsEnums.MAIN_STATUS mainStatus = _taskName == "" ? clsEnums.MAIN_STATUS.IDLE : clsEnums.MAIN_STATUS.RUN;
                                VMSManager.UpdatePartsAGVInfo(_agvName, _location);
                                if (PartsAGVStatueDtoStored.TryGetValue(_agvName, out var state))
                                {
                                    state.TaskName = _taskName;
                                    state.BatteryLevel_1 = (double)battery;
                                    state.StationName = _location;
                                    state.Connected = true;
                                    state.OnlineStatus = clsEnums.ONLINE_STATE.ONLINE;
                                    state.Simulation = false;
                                    state.MainStatus = mainStatus;
                                    state.CargoStatus = cstID == "" ? 0 : 1;
                                    state.CurrentCarrierID = cstID;
                                }
                                else
                                {
                                    PartsAGVStatueDtoStored.Add(_agvName, new clsAGVStateDto
                                    {
                                        AGV_Name = _agvName,
                                        TaskName = _taskName,
                                        BatteryLevel_1 = (double)battery,
                                        StationName = _location,
                                        Connected = true,
                                        OnlineStatus = clsEnums.ONLINE_STATE.ONLINE,
                                        MainStatus = mainStatus
                                    });
                                }
                            }

                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
                await Task.Delay(1000);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
        }
    }
}

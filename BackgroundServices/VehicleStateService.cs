
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.AGV;
using AGVSystemCommonNet6.AGVDispatch;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using VMSystem.Services;
using System.Collections.Concurrent;
using AutoMapper;
using AGVSystemCommonNet6.Alarm;
using Microsoft.EntityFrameworkCore;
using VMSystem.Extensions;

namespace VMSystem.BackgroundServices
{
    public class VehicleStateService : IHostedService, IDisposable
    {
        public static Dictionary<string, clsAGVStateDto> AGVStatueDtoStored => DatabaseCaches.Vehicle.VehicleStates.ToDictionary(v => v.AGV_Name, v => v);
        private ConcurrentDictionary<string, (DateTime time, double milegate)> VehiclesMileageStoreed = new();

        AGVSDbContext context;
        public VehicleStateService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
            context = _scopeFactory.CreateAsyncScope().ServiceProvider.GetRequiredService<AGVSDbContext>();
        }
        private readonly IServiceScopeFactory _scopeFactory;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            clsAGV.OnMileageChanged += ClsAGV_OnMileageChanged;
            _ = Task.Run(() => DoWork());
        }

        private async void ClsAGV_OnMileageChanged(object? sender, (IAGV agv, double currentMileage) e)
        {
            UpdateMaintainOdomOfHorizonMotor(e.agv, e.currentMileage);
        }

        private async Task UpdateMaintainOdomOfHorizonMotor(IAGV agv, double currentMileage)
        {
            await Task.Delay(1);

            if (VehiclesMileageStoreed.TryGetValue(agv.Name, out (DateTime time, double mileage) lastData))
            {
                if ((DateTime.Now - lastData.time).TotalSeconds > 30)
                {
                    VehicleMaintainService maintainService = _scopeFactory.CreateAsyncScope().ServiceProvider.GetRequiredService<VehicleMaintainService>();
                    double diffValue = (currentMileage - lastData.mileage);
                    await maintainService.UpdateHorizonMotorCurrentMileageValue(agv.Name, diffValue);
                    VehiclesMileageStoreed[agv.Name] = new(DateTime.Now, currentMileage);
                }
            }
            else
            {
                VehiclesMileageStoreed.TryAdd(agv.Name, new(DateTime.Now, currentMileage));
            }
        }

        private async Task DoWork()
        {
            // 配置 AutoMapper
            var config = new MapperConfiguration(cfg => cfg.CreateMap<clsAGVStateDto, clsAGVStateDto>());
            var mapper = config.CreateMapper();

            while (true)
            {
                await Task.Delay(150);
                try
                {
                    await VMSManager.AgvStateDbTableLock.WaitAsync();
                    foreach (IAGV agv in VMSManager.AllAGV)
                    {
                        clsAGVStateDto? entityInDatabase = context.AgvStates.Where(state => state.AGV_Name == agv.Name).FirstOrDefault();

                        if (entityInDatabase == null)//不存在
                        {
                            clsAGVStateDto _newEntity = UpdateDTO(ref entityInDatabase, agv);
                            context.AgvStates.Add(_newEntity);
                        }
                        else
                        {
                            clsAGVStateDto modifiedEntity = UpdateDTO(ref entityInDatabase, agv);
                            clsAGVStateDto statesCache = DatabaseCaches.Vehicle.VehicleStates.FirstOrDefault(v => v.AGV_Name == modifiedEntity.AGV_Name);
                            if (statesCache == null)
                                continue;

                            if (statesCache.HasChanged(modifiedEntity))
                            {
                                mapper.Map(modifiedEntity, entityInDatabase);
                                context.Entry(entityInDatabase).State = Microsoft.EntityFrameworkCore.EntityState.Modified;

                            }
                        }
                    }
                    int changedNum = await context.SaveChangesAsync();

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message + ex.StackTrace);
                }
                finally
                {
                    VMSManager.AgvStateDbTableLock.Release();
                }
            }

            clsAGVStateDto UpdateDTO(ref clsAGVStateDto? originalDto, IAGV agv)
            {
                GetStationsName(agv, out string currentLocDisplay, out string sourceDisplay, out string destineDisplay);
                clsAGVStateDto dto = originalDto ?? new clsAGVStateDto();
                dto.AGV_Name = agv.Name;
                dto.AGV_ID = agv.AgvIDStr;
                dto.Enabled = agv.options.Enabled;
                dto.BatteryLevel_1 = agv.states.Electric_Volume.Length >= 1 ? agv.states.Electric_Volume[0] : -1;
                dto.BatteryLevel_2 = agv.states.Electric_Volume.Length >= 2 ? agv.states.Electric_Volume[1] : -1;
                dto.OnlineStatus = agv.online_state;
                dto.MainStatus = agv.states.AGV_Status;
                dto.CargoStatus = agv.states.Cargo_Status;
                dto.CargoType = agv.states.CargoType;
                dto.CurrentCarrierID = agv.states.CSTID.Length == 0 ? "" : agv.states.CSTID[0];
                dto.CurrentLocation = agv.states.Last_Visited_Node.ToString();
                dto.Theta = Math.Round(agv.states.Coordination.Theta, 1);
                dto.Connected = agv.connected;
                dto.Group = agv.VMSGroup;
                dto.Model = agv.model;
                dto.TaskName = agv.main_state == MAIN_STATUS.RUN ? agv.taskDispatchModule.OrderHandler.OrderData.TaskName : "";
                dto.TaskRunAction = agv.taskDispatchModule.OrderHandler.OrderData.Action;
                dto.CurrentAction = agv.taskDispatchModule.OrderHandler.RunningTask.ActionType;
                dto.TransferProcess = agv.taskDispatchModule.OrderHandler.RunningTask.Stage;
                dto.IsCharging = agv.states.IsCharging;
                dto.IsExecutingOrder = agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
                dto.VehicleWidth = agv.options.VehicleWidth;
                dto.VehicleLength = agv.options.VehicleLength;
                dto.Protocol = agv.options.Protocol;
                dto.IP = agv.options.HostIP;
                dto.Port = agv.options.HostPort;
                dto.Simulation = agv.options.Simulation;
                dto.LowBatLvThreshold = agv.options.BatteryOptions.LowLevel;
                dto.MiddleBatLvThreshold = agv.options.BatteryOptions.MiddleLevel;
                dto.HighBatLvThreshold = agv.options.BatteryOptions.HightLevel;
                dto.TaskSourceStationName = sourceDisplay;
                dto.TaskDestineStationName = destineDisplay;
                dto.StationName = currentLocDisplay;
                dto.AppVersion = agv.states.AppVersion;
                return dto;
            };

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

        public async Task StopAsync(CancellationToken cancellationToken)
        {
        }

        public void Dispose()
        {
            //throw new NotImplementedException();
        }

    }
}

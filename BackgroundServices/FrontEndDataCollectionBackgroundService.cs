using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.SignalR;
using VMSystem.Services;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6;
using Newtonsoft.Json;
using AGVSystemCommonNet6.Configuration;
using static VMSystem.AGV.IAGV;
using System.Linq;

namespace VMSystem.BackgroundServices
{
    public class FrontEndDataCollectionBackgroundService : BackgroundService
    {
        private readonly IHubContext<FrontEndDataHub> _hubContext;
        public FrontEndDataCollectionBackgroundService(IHubContext<FrontEndDataHub> hubContext)
        {
            _hubContext = hubContext;
        }
        internal static object _previousData = new object();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(200);
                    object data = new object();
                    try
                    {

                        var _VMSStatus = VehicleStateService.AGVStatueDtoStored.Values.OrderBy(d => d.AGV_Name).ToList();
                        Dictionary<string, BATTERY_STATUS> vehicleBatStatus = VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => agv.batteryStatus);

                        if (PartsAGVInfoService.PartsAGVStatueDtoStored.Any())
                        {
                            _VMSStatus.AddRange(PartsAGVInfoService.PartsAGVStatueDtoStored.Values.OrderBy(d => d.AGV_Name).ToList());
                        }

                        data = new
                        {
                            AGVNaviPathsInfoVM = ViewModelFactory.GetAGVNaviPathsInfoVM(),
                            OtherAGVLocations = VMSManager.OthersAGVInfos.Values.ToList(),
                            VMSStatus = _VMSStatus,
                            VehicleBatteryStatus = vehicleBatStatus
                        };

                        if (!AGVSConfigulator.SysConfigs.BaseOnKGSWebAGVSystem && JsonConvert.SerializeObject(data).Equals(JsonConvert.SerializeObject(_previousData)))
                        {
                            data = null;
                            continue;
                        }
                        else
                        {
                            await Task.Delay(300);
                        }
                        _previousData = data.Clone();
                        _hubContext.Clients.All.SendAsync("ReceiveData", "VMS", data);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            });
        }

        private static class ViewModelFactory
        {
            public static object GetDynamicTrafficDataVM()
            {
                return TrafficControlCenter.DynamicTrafficState;
            }
            public static object GetAGVNaviPathsInfoVM()
            {
                object GetNavigationData(AGV.IAGV agv)
                {
                    if (agv.currentMapPoint == null)
                        return new { };
                    var OrderHandler = agv.taskDispatchModule.OrderHandler;
                    List<int> navingTagList = GetNavigationTag(agv);
                    bool isOrderExecuting = agv.taskDispatchModule.OrderExecuteState == AGV.clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
                    return new
                    {
                        currentLocation = agv.currentMapPoint.TagNumber,
                        currentCoordication = new
                        {
                            X = Math.Round(agv.states.Coordination.X, 1),
                            Y = Math.Round(agv.states.Coordination.Y, 1),
                            Theta = Math.Round(agv.states.Coordination.Theta, 1)
                        },
                        vehicleWidth = agv.options.VehicleWidth,
                        vehicleLength = agv.options.VehicleLength,
                        cargo_status = new
                        {
                            exist = agv.states.Cargo_Status == 1,
                            cargo_type = agv.states.CargoType,
                            cst_id = agv.states.CSTID.FirstOrDefault()
                        },
                        nav_path = isOrderExecuting ? navingTagList : new List<int>(),
                        theta = Math.Round(agv.states.Coordination.Theta, 1),
                        waiting_info = agv.taskDispatchModule.OrderHandler.RunningTask.TrafficWaitingState,
                        states = new
                        {
                            is_online = agv.online_state == ONLINE_STATE.ONLINE,
                            main_status = agv.main_state
                        },
                    };

                    List<int> GetNavigationTag(AGV.IAGV agv)
                    {
                        List<int> tags = agv.NavigationState.NextNavigtionPoints.GetTagCollection().DistinctBy(tag => tag).ToList();
                        return tags;
                    }
                }
                return VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => GetNavigationData(agv));
            }
            public static object GetVMSAliveCheckVM()
            {
                return true;
            }
            public static object GetVMSStatusData()
            {
                return VMSManager.GetVMSViewData();
            }
        }
    }
}

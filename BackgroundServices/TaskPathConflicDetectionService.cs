
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.BackgroundServices
{
    public class TaskPathConflicDetectionService : IHostedService
    {
        ILogger<TaskPathConflicDetectionService> logger;
        public TaskPathConflicDetectionService(ILogger<TaskPathConflicDetectionService> logger)
        {
            this.logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                logger.LogInformation("TaskPathConflicDetectionService is starting.");
                while (true)
                {
                    await Task.Delay(1);

                    //監視每一台車當前的軌跡，若有軌跡衝突則進行處理
                    foreach (var vehicle in VMSManager.AllAGV)
                    {
                        if (vehicle.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                            continue;

                        await _TryGetConflic(vehicle);
                    }


                    async Task _TryGetConflic(IAGV vehicle)
                    {
                        IEnumerable<IAGV> otherVehicles = VMSManager.AllAGV.FilterOutAGVFromCollection(vehicle);
                        IEnumerable<MapPoint> trajectoryRunning(IAGV _vehicle)
                        {
                            return _vehicle.NavigationState.NextNavigtionPoints;
                        }

                        IEnumerable<IAGV> conflicVehicles = otherVehicles.Where(agv => agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                                                                         .Where(agv => trajectoryRunning(agv).GetTagCollection().Intersect(trajectoryRunning(vehicle).GetTagCollection()).Any());

                        if (conflicVehicles.Any())
                        {
                            if (vehicle.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None)
                            {
                                logger.LogCritical($"Vehicle {vehicle.Name} has path conflic with {string.Join(",", conflicVehicles.Select(agv => agv.Name))}");
                                AlarmManagerCenter.AddAlarmAsync(ALARMS.VEHICLES_TRAJECTORY_CONFLIC, level: ALARM_LEVEL.WARNING);
                               vehicle.CurrentRunningTask().CycleStopRequestAsync();
                            }
                            //vehicle.TaskExecuter.EmergencyStop();
                            //foreach (var conflicVehicle in conflicVehicles)
                            //{
                            //    conflicVehicle.TaskExecuter.EmergencyStop();
                            //}
                            //await Task.Delay(1000);
                        }
                        else
                        {
                            vehicle.CurrentRunningTask().TaskExecutePauseMRE.Set();
                        }
                    };
                }
            });
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

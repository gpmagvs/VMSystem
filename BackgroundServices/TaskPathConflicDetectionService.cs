
using AGVSystemCommonNet6;
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
                        List<IAGV> conflicVehicles_BodyCollision = otherVehicles.Where(agv => vehicle.NavigationState.NextPathOccupyRegions.Count > 2)
                                                                                .Where(agv => vehicle.NavigationState.NextPathOccupyRegions.Skip(1).Any(reg => reg.IsIntersectionTo(agv.AGVRealTimeGeometery)))
                                                                                .ToList();
                        if (conflicVehicles_BodyCollision.Any())
                        {
                            bool _IsEmergencyStop = false;
                            if (conflicVehicles_BodyCollision.Any(v => vehicle.states.Coordination.CalculateDistance(v.states.Coordination) < 2))
                            {
                                _IsEmergencyStop = true;
                                //vehicle.main_state = AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN;
                                //AlarmManagerCenter.AddAlarmAsync(ALARMS.Path_Conflic_But_Dispatched, Equipment_Name: vehicle.Name, location: vehicle.currentMapPoint.Graph.Display, taskName: vehicle.CurrentRunningTask().OrderData.TaskName, level: ALARM_LEVEL.ALARM);
                                //vehicle.TaskExecuter.EmergencyStop();
                                await Task.Delay(1000);
                            }
                            else
                            {
                                _IsEmergencyStop = false;
                                //AlarmManagerCenter.AddAlarmAsync(ALARMS.Path_Conflic_But_Dispatched, Equipment_Name: vehicle.Name, location: vehicle.currentMapPoint.Graph.Display, taskName: vehicle.CurrentRunningTask().OrderData.TaskName, level: ALARM_LEVEL.WARNING);
                                //vehicle.CurrentRunningTask().CycleStopRequestAsync();
                                await Task.Delay(1000);
                            }
                            LogVehicleStatus(vehicle, conflicVehicles_BodyCollision, _IsEmergencyStop);

                        }
                        if (conflicVehicles.Any())
                        {
                            if (vehicle.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None)
                            {
                                logger.LogCritical($"Vehicle {vehicle.Name} has path conflic with {string.Join(",", conflicVehicles.Select(agv => agv.Name))}");
                                LogVehicleStatus(vehicle, conflicVehicles.ToList(), false);
                                //AlarmManagerCenter.AddAlarmAsync(ALARMS.VEHICLES_TRAJECTORY_CONFLIC, level: ALARM_LEVEL.WARNING);
                                //vehicle.CurrentRunningTask().CycleStopRequestAsync();
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
                            //vehicle.CurrentRunningTask().TaskExecutePauseMRE.Set();
                        }
                    };
                }
            });
        }
        private void LogVehicleStatus(IAGV mainVehicles, List<IAGV> conflicVehiclesBodyCollision, bool _IsEmergencyStop)
        {

            string mainVehicleStatusLog = _GetStatus(ref mainVehicles);
            string otherVehicleStatusLog = string.Join("\r\n", conflicVehiclesBodyCollision.Select(agv => _GetStatus(ref agv)));
            logger.LogCritical($"Vehicle {mainVehicles.Name} has path conflic with {string.Join(",", conflicVehiclesBodyCollision.Select(agv => agv.Name))} [{(_IsEmergencyStop ? "EMERGENCY STOP" : "CYCLE STOP")}]\r\n" +
                $"Main Vehicle Status:{mainVehicleStatusLog}\r\n" +
                $"Other Vehicle Status:{otherVehicleStatusLog}");
            string _GetStatus(ref IAGV agv)
            {
                return $"[{agv.Name}] Main Status:{agv.main_state},Online Status:{agv.online_state},Coordination:{agv.states.Coordination.ToString()},Tag:{agv.currentMapPoint.TagNumber}";
            }
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

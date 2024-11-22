using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Maintainance;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.VMS;
using AGVSystemCommonNet6.Notify;
using AGVSystemCommonNet6.ViewModels;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop.Infrastructure;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.BackgroundServices;
using VMSystem.Extensions;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static VMSystem.AGV.clsGPMInspectionAGV;

namespace VMSystem.VMS
{
    public class clsAGVStatusSimple
    {
        public string AGVName { get; set; }
        public string Location { get; set; }
    }
    public partial class VMSManager
    {
        public static AGVSDbContext AGVSDbContext { get; internal set; }
        public static GPMForkAgvVMS ForkAGVVMS;
        public static Dictionary<VMS_GROUP, VMSAbstract> VMSList = new Dictionary<VMS_GROUP, VMSAbstract>();
        public static clsOptimizeAGVDispatcher OptimizeAGVDisaptchModule = new clsOptimizeAGVDispatcher();
        public static Dictionary<string, clsAGVStatusSimple> OthersAGVInfos = new Dictionary<string, clsAGVStatusSimple>();
        internal static SemaphoreSlim tasksLock = new SemaphoreSlim(1, 1);
        internal static SemaphoreSlim AgvStateDbTableLock = new SemaphoreSlim(1, 1);
        public static List<IAGV> AllAGV
        {
            get
            {
                List<IAGV> outputs = new List<IAGV>();
                foreach (var vms in VMSList.Values)
                {
                    if (vms == null)
                        continue;
                    outputs.AddRange(vms.AGVList.Values.ToArray());

                }

                return outputs.FindAll(agv => agv.options != null && agv.options.Enabled);
            }
        }
        internal static List<IAGV> RunningAGVList
        {
            get
            {
                List<IAGV> outputs = new List<IAGV>();
                outputs.AddRange(ForkAGVVMS.AGVList.Values.ToList().FindAll(agv => agv.main_state == clsEnums.MAIN_STATUS.RUN).ToArray());
                return outputs;
            }
        }

        internal static async Task Initialize()
        {
            await Task.Delay(1);
            TcpServerInit();

            //clsTaskDatabaseWriteableAbstract.OnTaskDBChangeRequestRaising += HandleTaskDBChangeRequestRaising;
            DeepCharger.OnDeepChargeRequestRaised += DeepCharger_OnDeepChargeRequestRaised;

            var agvList = AGVSDbContext.AgvStates.ToList();
            var forkAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.FORK);
            var submarineAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.SUBMERGED_SHIELD);
            var inspectionAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.INSPECTION_AGV);
            var yunTechForkAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.YUNTECH_FORK_AGV);


            await MaintainSettingInitialize();
            VMSList.Add(VMS_GROUP.GPM_FORK, new GPMForkAgvVMS(forkAgvList.Select(agv => new clsAGV(agv.AGV_Name, CreateOptions(agv))).ToList()));
            VMSList.Add(VMS_GROUP.GPM_SUBMARINE_SHIELD, new GPMSubmarine_ShieldVMS(submarineAgvList.Select(agv => new clsGPMSubmarine_Shield(agv.AGV_Name, CreateOptions(agv))).ToList()));
            VMSList.Add(VMS_GROUP.GPM_INSPECTION_AGV, new GPMInspectionAGVVMS(inspectionAgvList.Select(agv => new clsGPMInspectionAGV(agv.AGV_Name, CreateOptions(agv))).ToList()));
            VMSList.Add(VMS_GROUP.YUNTECH_FORK, new YunTechAgvVMS(yunTechForkAgvList.Select(agv => new clsYunTechAGV(agv.AGV_Name, CreateOptions(agv))).ToList()));

            List<Task> tasks = new List<Task>();

            tasks.Add(VMSList[VMS_GROUP.GPM_FORK].StartAGVs());
            tasks.Add(VMSList[VMS_GROUP.GPM_SUBMARINE_SHIELD].StartAGVs());
            tasks.Add(VMSList[VMS_GROUP.GPM_INSPECTION_AGV].StartAGVs());
            tasks.Add(VMSList[VMS_GROUP.YUNTECH_FORK].StartAGVs());

            await Task.WhenAll(tasks);
            var _object = VMSList.ToDictionary(grop => grop.Key, grop => new { AGV_List = grop.Value.AGVList.ToDictionary(a => a.Key, a => a.Value.options) });
            OptimizeAGVDisaptchModule.Run();
            TaskAssignToAGVWorker();
        }

        private static void DeepCharger_OnDeepChargeRequestRaised(object? sender, DeepCharger.DeepChargeRequsetDto requsetDto)
        {
            bool accept = ConfirmDeepChargeExecutable(requsetDto.Agv.Name, out string messsage);

            requsetDto.Accept = accept;
            requsetDto.Message = messsage;

            if (accept)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await tasksLock.WaitAsync();
                        //create a deep charge task and add to db
                        clsTaskDto _order = new clsTaskDto()
                        {
                            Action = ACTION_TYPE.DeepCharge,
                            DesignatedAGVName = requsetDto.Agv.Name,
                            RecieveTime = DateTime.Now,
                            TaskName = $"DeepCharge_{requsetDto.Agv.Name}_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                            Priority = 100,
                        };
                        AGVSDbContext.Tasks.Add(_order);
                        await AGVSDbContext.SaveChangesAsync();
                    }
                    catch (Exception)
                    {

                        throw;
                    }
                    finally
                    {
                        tasksLock.Release();
                    }

                });
            }

        }

        private static async Task MaintainSettingInitialize()
        {
            List<MAINTAIN_ITEM> allMaintainItems = Enum.GetValues(typeof(MAINTAIN_ITEM)).Cast<MAINTAIN_ITEM>().ToList();
            try
            {
                var maintainSettingNotCompletesAGVs = await AGVSDbContext.AgvStates.Include(v => v.MaintainSettings).Where(agv => agv.MaintainSettings.Count != allMaintainItems.Count).ToListAsync();
                if (maintainSettingNotCompletesAGVs.Any())
                {
                    foreach (var item in maintainSettingNotCompletesAGVs)
                    {
                        AddMaintainSettings(item);
                    }
                }
            }
            catch (Exception ex)
            {
                LOG.Critical("[VMSManager.MaintainSettingInitialize] with exception" + ex);
            }
            await AGVSDbContext.SaveChangesAsync();
            void AddMaintainSettings(clsAGVStateDto vehicleState)
            {
                try
                {
                    if (vehicleState.MaintainSettings == null)
                        vehicleState.MaintainSettings = new List<VehicleMaintain>();

                    List<MAINTAIN_ITEM> existMaintainItems = vehicleState.MaintainSettings.Any() ?
                                                            vehicleState.MaintainSettings.Select(item => item.MaintainItem).ToList() : new();
                    foreach (var maintainItem in allMaintainItems)
                    {
                        if (existMaintainItems.Contains(maintainItem))
                            continue;
                        var agv = AGVSDbContext.AgvStates.AsNoTracking().First(a => a.AGV_Name == vehicleState.AGV_Name);
                        agv.MaintainSettings.Add(new VehicleMaintain(vehicleState.AGV_Name, maintainItem));
                    }
                }
                catch (Exception ex)
                {

                    throw ex;
                }
            }
        }

        private static clsAGVOptions CreateOptions(clsAGVStateDto agvDto)
        {
            int previousTag = 0;
            int.TryParse(agvDto.CurrentLocation, out previousTag);
            return new clsAGVOptions
            {
                Enabled = agvDto.Enabled,
                HostIP = agvDto.IP,
                HostPort = agvDto.Port,
                VehicleWidth = agvDto.VehicleWidth,
                VehicleLength = agvDto.VehicleLength,
                Simulation = agvDto.Simulation,
                Protocol = agvDto.Protocol,
                InitTag = previousTag,
                BatteryOptions = new clsBatteryOptions
                {
                    LowLevel = agvDto.LowBatLvThreshold,
                    MiddleLevel = agvDto.MiddleBatLvThreshold,
                    HightLevel = agvDto.HighBatLvThreshold
                }
            };
        }

        private static void TcpServerInit()
        {
            TcpServer.OnClientConnected += TcpServer_OnClientConnected;
            Thread thread = new Thread(async () =>
            {
                try
                {
                    if (await TcpServer.Connect())
                    {
                        LOG.INFO($"TCP/IP Server build done({TcpServer.IP}:{TcpServer.VMSPort})");
                    }
                    else
                    {

                    }
                }
                catch (Exception ex)
                {
                    LOG.ERROR(ex);
                }
            });
            thread.Start();
        }

        private static ConcurrentQueue<clsTaskDto> WaitingForWriteToTaskDatabaseQueue = new ConcurrentQueue<clsTaskDto>();
        public static void HandleTaskDBChangeRequestRaising(object? sender, clsTaskDto task_data_dto)
        {
            WaitingForWriteToTaskDatabaseQueue.Enqueue(task_data_dto);
        }

        private static void TaskAssignToAGVWorker()
        {
            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    await Task.Delay(100);
                    try
                    {
                        foreach (var _agv in VMSManager.AllAGV)
                        {
                            await Task.Delay(10);
                            if (_agv.taskDispatchModule == null)
                                continue;

                            var taskNamesStored = _agv.taskDispatchModule.taskList.Select(task => task.TaskName).ToList();
                            var tasks = DatabaseCaches.TaskCaches.WaitExecuteTasks.Where(_task => !taskNamesStored.Contains(_task.TaskName))
                                                                                  .Where(_task => _task.DesignatedAGVName == _agv.Name);
                            if (tasks.Any())
                                _agv.taskDispatchModule.TryAppendTasksToQueue(tasks.ToList());
                            // var endTasks = database.tables.Tasks.Where(_task => (_task.State == TASK_RUN_STATUS.CANCEL || _task.State == TASK_RUN_STATUS.FAILURE) && _task.DesignatedAGVName == _agv.Name).AsNoTracking();
                        }

                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                    finally
                    {
                    }

                }
            });
        }
        internal static bool GetAGVByName(string AGVName, out IAGV agv)
        {
            agv = GetAGVByName(AGVName);
            return agv != null;
        }
        internal static IAGV GetAGVByName(string AGVName)
        {
            return AllAGV.FirstOrDefault(agv => agv.Name == AGVName);
        }
        public static bool TryGetAGV(string AGVName, out IAGV agv)
        {
            agv = null;
            var agvList = SearchAGVByName(AGVName);

            if (agvList.Count == 0)
            {
                return false;
            }
            else
            {
                agv = agvList[0];
                return true;
            }
        }


        public static List<IAGV> SearchAGVByName(string agv_name)
        {
            if (AllAGV.Count == 0)
                return new List<IAGV>();
            return AllAGV.FindAll(agv => agv.Name == agv_name);
        }

        internal static List<VMSViewModel> GetVMSViewData()
        {

            var outputs = new List<VMSViewModel>();
            foreach (var agv in AllAGV)
            {
                outputs.Add(new VMSViewModel()
                {
                    BaseProps = new AGVSystemCommonNet6.VMSBaseProp
                    {
                        AGV_Name = agv.Name,
                    },
                    OnlineStatus = agv.online_state,
                    RunningStatus = agv.states,
                });
            }

            return outputs;
        }

        /// <summary>
        /// 找一台最佳的AGV來執行任務
        /// </summary>
        /// <param name="taskData"></param>
        /// <param name="agv"></param>
        /// <returns></returns>
        public static bool TryRequestAGVToExecuteTask(ref clsTaskDto taskData, out IAGV agv)
        {
            agv = null;
            string agvname = taskData.DesignatedAGVName;
            string to_Station = taskData.To_Station;
            if (agvname != "")
            {
                var _agv = AllAGV.FirstOrDefault(agv => agv.Name == agvname);
                if (_agv != null)
                {
                    agv = _agv;
                    bool isAGVIdling = _agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.IDLE;
                    bool isAGVOnline = _agv.online_state == AGVSystemCommonNet6.clsEnums.ONLINE_STATE.ONLINE;
                }
            }
            else
            {
                //先找IDLE中的
                var idlingAGVList = AllAGV.FindAll(agv => agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.IDLE);
                if (idlingAGVList.Count > 0)
                {
                    //找路徑最短的
                    IOrderedEnumerable<IAGV> orderByDistanceCost = idlingAGVList.OrderBy(_agv => _agv.CalculatePathCost(StaMap.Map, to_Station));
                    if (orderByDistanceCost.Count() > 0)
                    {
                        agv = orderByDistanceCost.First();
                    }
                }
                else
                {
                    //找任務鍊最短的
                    IOrderedEnumerable<IAGV> orderedByTaskCounts = AllAGV.OrderBy(agv => agv.taskDispatchModule.taskList.Count);
                    agv = orderedByTaskCounts.First();
                }
            }


            if (agv != null)
            {
                if ((taskData.Action == ACTION_TYPE.Charge | taskData.Action == ACTION_TYPE.Park) && taskData.To_Station == "-1")
                {

                }

                if (CheckTaskDataValid(agv, ref taskData))
                {
                    return CheckCSTStateByAction(agv, taskData.Action);
                }
                else
                    return false;
            }
            else
            {
                return false;
            }
        }

        private static bool CheckCSTStateByAction(IAGV agv, ACTION_TYPE action)
        {
            bool IsAGVHasCarrier = agv.states.Cargo_Status != 0;
            if (action == ACTION_TYPE.Load)//放貨
                if (!IsAGVHasCarrier)
                {
                    agv.AddNewAlarm(ALARMS.CST_STATUS_CHECK_FAIL, ALARM_SOURCE.AGVS);
                    return false;
                }

            if (action == ACTION_TYPE.Unload | action == ACTION_TYPE.Carry | action == ACTION_TYPE.Charge)
                if (IsAGVHasCarrier)
                {
                    agv.AddNewAlarm(ALARMS.CST_STATUS_CHECK_FAIL, ALARM_SOURCE.AGVS);
                    return false;
                }

            return true;
        }

        /// <summary>
        /// 檢查任務資料是否有異常的部分
        /// </summary>
        /// <param name="taskData"></param>
        /// <returns></returns>
        internal static bool CheckTaskDataValid(IAGV agv, ref clsTaskDto taskData)
        {

            ACTION_TYPE action = taskData.Action;
            bool isFromTagFormated = int.TryParse(taskData.From_Station, out int FromStationTag);
            bool isToTagFormated = int.TryParse(taskData.To_Station, out int ToStationTag);
            if (action == ACTION_TYPE.Carry && (!isToTagFormated | !isFromTagFormated)) //
            {
                return false;
            }
            else if (!isToTagFormated)
                return false;
            if (FromStationTag == -1)
                FromStationTag = agv.states.Last_Visited_Node;
            //
            if (action == ACTION_TYPE.Charge)
            {
                if (agv.main_state == clsEnums.MAIN_STATUS.Charging)
                {
                    return true;
                }
                if (ToStationTag == -1)
                {

                    List<MapPoint> chargeableStations = StaMap.GetChargeableStations();
                    //chargeableStations = chargeableStations.FindAll(sta => ChargeableMatch(sta));
                    //先不考慮交通問題 挑一個最近的
                    StaMap.TryGetPointByTagNumber(FromStationTag, out MapPoint fromStation);
                    var distances = chargeableStations.ToDictionary(st => st.Name, st => st.CalculateDistance(fromStation.X, fromStation.Y));
                    LOG.INFO(string.Join("\r\n", distances.Select(d => d.Key + "=>" + d.Value).ToArray()));
                    chargeableStations = chargeableStations.OrderBy(st => st.CalculateDistance(fromStation.X, fromStation.Y)).ToList();
                    if (chargeableStations.Count > 0)
                    {
                        ToStationTag = chargeableStations.First().TagNumber;
                        taskData.To_Station = ToStationTag.ToString();
                    }
                    else
                        AlarmManagerCenter.AddAlarmAsync(ALARMS.NO_AVAILABLE_CHARGE_PILE, level: ALARM_LEVEL.WARNING, taskName: taskData.TaskName);
                }
            }
            if (action == ACTION_TYPE.None)
                FromStationTag = agv.states.Last_Visited_Node;
            return CheckStationTypeMatch(action, FromStationTag, ToStationTag);
            //Check Action and Final Station Information

        }

        private static bool CheckStationTypeMatch(ACTION_TYPE action, int FromStationTag, int ToStationTag)
        {
            MapPoint from_station = StaMap.Map.Points.Values.FirstOrDefault(st => st.TagNumber == FromStationTag);
            MapPoint to_station = StaMap.Map.Points.Values.FirstOrDefault(st => st.TagNumber == ToStationTag);

            STATION_TYPE from_station_type = from_station.StationType;
            STATION_TYPE to_station_type = to_station.StationType;


            if (to_station == null)
                return false;
            if (action == ACTION_TYPE.Charge && !to_station.IsChargeAble())
                return false;
            else if (action == ACTION_TYPE.None && (to_station_type != STATION_TYPE.Normal))
                return false;
            else if (action == ACTION_TYPE.Load && !to_station.IsLoadAble())
                return false;
            else if (action == ACTION_TYPE.Unload && !to_station.IsUnloadAble())
                return false;

            else if (action == ACTION_TYPE.Carry)
            {
                if (from_station == null) return false;

                if (!from_station.IsUnloadAble() | !to_station.IsLoadAble())
                    return false;
            }

            return true;
        }

        internal static MapPoint GetAGVCurrentMapPointByName(string AGVName)
        {
            var agv = AllAGV.FirstOrDefault(agv => agv.Name == AGVName);
            if (agv != null)
                return agv.currentMapPoint;
            else
                return null;
        }

        internal static async Task<(bool confirm, string message)> TryLocatingAGVAsync(string agv_name, clsLocalizationVM localizationVM)
        {

            IAGV agv = GetAGVByName(agv_name);
            if (agv.model == AGV_TYPE.INSPECTION_AGV)
                return await agv.Locating(localizationVM);
            else if (agv.options.Simulation)
            {
                agv.AgvSimulation.runningSTatus.Last_Visited_Node = localizationVM.currentID;
                var _mapPoint = StaMap.GetPointByTagNumber(localizationVM.currentID);
                agv.AgvSimulation.runningSTatus.Coordination.X = _mapPoint.X;
                agv.AgvSimulation.runningSTatus.Coordination.Y = _mapPoint.Y;
                StaMap.RegistPoint(agv.Name, _mapPoint, out _);
                return (true, "");
            }
            else
                return (false, "");
        }

        internal static async Task<(bool confirm, string message)> AddVehicle(clsAGVStateDto dto)
        {
            var group = GetGroup(dto.Model);
            dto.Group = group;
            dto.Enabled = true;

            var option = CreateOptions(dto);

            if (VMSList[group].AGVList.Any(agv => agv.Value.Name == dto.AGV_Name))
            {
                return (false, $"已存在ID為 {dto.AGV_Name}的車輛!");
            }

            IAGV agv = null;
            switch (dto.Model)
            {
                case AGV_TYPE.FORK:
                    agv = new clsAGV(dto.AGV_Name, option);
                    break;
                case AGV_TYPE.YUNTECH_FORK_AGV:
                    agv = new clsYunTechAGV(dto.AGV_Name, option);
                    break;
                case AGV_TYPE.INSPECTION_AGV:
                    agv = new clsGPMInspectionAGV(dto.AGV_Name, option);
                    break;
                case AGV_TYPE.SUBMERGED_SHIELD:
                    agv = new clsGPMSubmarine_Shield(dto.AGV_Name, option);
                    break;
                case AGV_TYPE.SUBMERGED_SHIELD_Parts:
                    agv = new clsGPMSubmarine_Shield(dto.AGV_Name, option);
                    break;
                case AGV_TYPE.Any:
                    break;
                default:
                    break;
            }

            agv.VMSGroup = dto.Group;
            agv.options.Enabled = true;
            agv?.Run();
            VMSList[dto.Group].AGVList.Add(agv.Name, agv);
            try
            {
                await AgvStateDbTableLock.WaitAsync();
                AGVSDbContext.AgvStates.Add(dto);
                await AGVSDbContext.SaveChangesAsync();
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                AgvStateDbTableLock.Release();
            }
        }

        internal static async Task<(bool confirm, string message)> DeleteVehicle(string aGV_Name)
        {
            try
            {
                await AgvStateDbTableLock.WaitAsync();
                var existData = AGVSDbContext.AgvStates.FirstOrDefault(agv => agv.AGV_Name == aGV_Name);
                if (existData == null)
                {
                    VehicleStateService.AGVStatueDtoStored.Remove(aGV_Name);
                    return (true, "");
                }

                AGVSDbContext.VehicleMaintain.RemoveRange(AGVSDbContext.VehicleMaintain.Where(m => m.AGV_Name == aGV_Name));
                await AGVSDbContext.SaveChangesAsync();
                AGVSDbContext.AgvStates.Remove(existData);

                var group = VMSList.FirstOrDefault(kpair => kpair.Value.AGVList.ContainsKey(aGV_Name));
                if (group.Value != null)
                {
                    var agv = group.Value.AGVList[aGV_Name];
                    agv.AgvSimulation?.Dispose();
                    group.Value.AGVList.Remove(aGV_Name);
                }
                await AGVSDbContext.SaveChangesAsync();
                VehicleStateService.AGVStatueDtoStored.Remove(aGV_Name);
                return (true, "");

            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                AgvStateDbTableLock.Release();
            }

        }
        internal static async Task<(bool confirm, string message)> EditVehicle(clsAGVStateDto dto, string ordAGVName)
        {
            try
            {
                await AgvStateDbTableLock.WaitAsync();
                var databaseDto = AGVSDbContext.AgvStates.FirstOrDefault(agv => agv.AGV_Name == ordAGVName);
                if (databaseDto != null)
                {
                    var group = GetGroup(dto.Model);
                    var oriAGV = AllAGV.FirstOrDefault(agv => agv.Name == ordAGVName);

                    if (oriAGV == null)
                    {
                        return await AddVehicle(dto);
                    }
                    databaseDto.IP = oriAGV.options.HostIP = dto.IP;
                    databaseDto.Port = oriAGV.options.HostPort = dto.Port;
                    databaseDto.Protocol = oriAGV.options.Protocol = dto.Protocol;
                    databaseDto.Model = oriAGV.model = dto.Model;
                    databaseDto.Group = group;
                    databaseDto.InitTag = oriAGV.options.InitTag = dto.InitTag;
                    oriAGV.Name = dto.AGV_Name;
                    databaseDto.VehicleLength = oriAGV.options.VehicleLength = dto.VehicleLength;
                    databaseDto.VehicleWidth = oriAGV.options.VehicleWidth = dto.VehicleWidth;
                    if (dto.Simulation && !oriAGV.options.Simulation)
                    {
                        oriAGV.AgvSimulation = new clsAGVSimulation((clsAGVTaskDisaptchModule)oriAGV.taskDispatchModule);
                        oriAGV.AgvSimulation.StartSimulation();
                    }
                    else if (!dto.Simulation && oriAGV.options.Simulation)
                    {
                        oriAGV.AgvSimulation.Dispose();
                    }
                    databaseDto.Simulation = oriAGV.options.Simulation = dto.Simulation;
                    await AGVSDbContext.SaveChangesAsync();
                }
                else
                {
                    AGVSDbContext.AgvStates.Add(dto);
                    await AGVSDbContext.SaveChangesAsync();
                }
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                AgvStateDbTableLock.Release();
            }

        }
        private static VMS_GROUP GetGroup(AGV_TYPE agv_model)
        {
            switch (agv_model)
            {
                case AGV_TYPE.FORK:
                    return VMS_GROUP.GPM_FORK;
                case AGV_TYPE.SUBMERGED_SHIELD:
                    return VMS_GROUP.GPM_SUBMARINE_SHIELD;
                case AGV_TYPE.INSPECTION_AGV:
                    return VMS_GROUP.GPM_INSPECTION_AGV;
                default:
                    return VMS_GROUP.GPM_SUBMARINE_SHIELD;
            }
        }

        private static async Task<bool> SaveVehicleInfoToDatabase(clsAGVStateDto dto)
        {
            using AGVSDatabase database = new AGVSDatabase();
            database.tables.AgvStates.Add(dto);
            int changeInt = await database.SaveChanges();
            return changeInt == 1;
        }

        internal static void UpdatePartsAGVInfo(string aGVName, string location)
        {
            var _agvName = aGVName.TrimEnd(new char[1] { ' ' });
            var _location = location.TrimEnd(new char[1] { ' ' });
            var allPointDisplayTexts = StaMap.Map.Points.Values.Select(pt => pt.Graph.Display);
            if (!allPointDisplayTexts.Any(txt => txt == _location))
                return;
            if (OthersAGVInfos.TryGetValue(_agvName, out var info))
            {
                bool _hasLocationChange = info.Location != _location;
                info.Location = _location;
                if (_hasLocationChange)
                {
                    LOG.TRACE($"Parts AGV-{_agvName} Location change to {_location}");
                }
            }
            else
            {
                LOG.TRACE($"Parts AGV-{_agvName} Location change to {_location}");
                OthersAGVInfos.Add(_agvName, new clsAGVStatusSimple
                {
                    Location = _location,
                    AGVName = _agvName
                });
            }
        }

        internal static async Task<bool> TaskCancel(string task_name, string reason)
        {
            try
            {
                await tasksLock.WaitAsync();
                IAGV vehicle = AllAGV.FirstOrDefault(agv => agv.CurrentRunningTask().OrderData.TaskName == task_name);
                if (vehicle == null)
                {
                    AGVSDbContext.Tasks.Where(tk => tk.TaskName == task_name).ToList().ForEach(tk =>
                    {
                        tk.FinishTime = DateTime.Now;
                        tk.FailureReason = reason;
                        tk.State = TASK_RUN_STATUS.CANCEL;
                    });
                    await AGVSDbContext.SaveChangesAsync();
                    return true;
                }
                await vehicle.CancelTaskAsync(task_name, reason);
                return true;

            }
            catch (Exception ex)
            {
                LOG.ERROR(ex);
                return false;
            }
            finally
            {
                tasksLock.Release();
            }

        }

        internal static async Task<(bool success, string message)> RemoveVehicleFromMap(string aGV_Name)
        {
            IAGV Vehicle = GetAGVByName(aGV_Name);
            if (Vehicle == null)
                return (false, $"{aGV_Name} Not Exist");


            var agvRegistes = StaMap.RegistDictionary.Where(v => v.Value.RegisterAGVName == aGV_Name);
            var removeds = agvRegistes.Where(v => StaMap.RegistDictionary.Remove(v.Key)).ToList();
            Vehicle.currentMapPoint = new MapPoint
            {
                TagNumber = 0,
                X = 1000,
                Y = 1000
            };
            Vehicle.states.Coordination.X = Vehicle.currentMapPoint.X;
            Vehicle.states.Coordination.Y = Vehicle.currentMapPoint.Y;

            //if (Vehicle.AgvSimulation != null)
            //    Vehicle.AgvSimulation.SetTag(556988);
            NotifyServiceHelper.INFO($"已解除{aGV_Name}註冊點Tag  {string.Join(",", removeds.Select(v => v.Key))}");
            return (true, "");
        }

        internal static void StopDeepCharge(string agvName)
        {
            IAGV Vehicle = GetAGVByName(agvName);
            if (Vehicle == null)
                return;
            Vehicle.StopDeepCharge();
        }

        /// <summary>
        /// 確認是否可以指派車輛進行深度充電
        /// </summary>
        /// <param name="agvName"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        internal static bool ConfirmDeepChargeExecutable(string agvName, out string message)
        {
            message = string.Empty;


            return true;
        }
    }
}

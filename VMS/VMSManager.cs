using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.VMS;
using AGVSystemCommonNet6.ViewModels;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Linq;
using VMSystem.AGV;
using static AGVSystemCommonNet6.clsEnums;
using static VMSystem.AGV.clsGPMInspectionAGV;

namespace VMSystem.VMS
{
    public partial class VMSManager
    {
        private const string Vehicle_Json_file = @"C:\AGVS\Vehicle.json";
        public static GPMForkAgvVMS ForkAGVVMS;
        public static Dictionary<VMS_GROUP, VMSAbstract> VMSList = new Dictionary<VMS_GROUP, VMSAbstract>();
        public static clsOptimizeAGVDispatcher OptimizeAGVDisaptchModule = new clsOptimizeAGVDispatcher();

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

        internal static async void Initialize(ConfigurationManager configuration)
        {
            TcpServerInit();

            clsTaskDatabaseWriteableAbstract.OnTaskDBChangeRequestRaising += HandleTaskDBChangeRequestRaising;

            using AGVSDatabase database = new AGVSDatabase();

            var agvList = database.tables.AgvStates.ToList();

            var forkAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.FORK);
            var submarineAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.SUBMERGED_SHIELD);
            var inspectionAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.INSPECTION_AGV);
            var yunTechForkAgvList = agvList.Where(agv => agv.Model == AGV_TYPE.YUNTECH_FORK_AGV);

            VMSList.Add(VMS_GROUP.GPM_FORK, new GPMForkAgvVMS(forkAgvList.Select(agv => new clsGPMForkAGV(agv.AGV_Name, CreateOptions(agv))).ToList()));
            VMSList.Add(VMS_GROUP.GPM_SUBMARINE_SHIELD, new GPMSubmarine_ShieldVMS(submarineAgvList.Select(agv => new clsGPMSubmarine_Shield(agv.AGV_Name, CreateOptions(agv))).ToList()));
            VMSList.Add(VMS_GROUP.GPM_INSPECTION_AGV, new GPMInspectionAGVVMS(inspectionAgvList.Select(agv => new clsGPMInspectionAGV(agv.AGV_Name, CreateOptions(agv))).ToList()));
            VMSList.Add(VMS_GROUP.YUNTECH_FORK, new YunTechAgvVMS(inspectionAgvList.Select(agv => new clsYunTechAGV(agv.AGV_Name, CreateOptions(agv))).ToList()));

            List<Task> tasks = new List<Task>();

            tasks.Add(VMSList[VMS_GROUP.GPM_FORK].StartAGVs());
            tasks.Add(VMSList[VMS_GROUP.GPM_SUBMARINE_SHIELD].StartAGVs());
            tasks.Add(VMSList[VMS_GROUP.GPM_INSPECTION_AGV].StartAGVs());
            tasks.Add(VMSList[VMS_GROUP.YUNTECH_FORK].StartAGVs());

            await Task.WhenAll(tasks);
            var _object = VMSList.ToDictionary(grop => grop.Key, grop => new { AGV_List = grop.Value.AGVList.ToDictionary(a => a.Key, a => a.Value.options) });
            VMSSerivces.SaveVMSVehicleGroupSetting(Vehicle_Json_file, JsonConvert.SerializeObject(_object, Formatting.Indented));

            OptimizeAGVDisaptchModule.Run();
            AGVStatesStoreWorker();
            TaskDatabaseChangeWorker();
            TaskAssignToAGVWorker();
        }
        private static clsAGVOptions CreateOptions(clsAGVStateDto agvDto)
        {
            return new clsAGVOptions
            {
                Enabled = agvDto.Enabled,
                HostIP = agvDto.IP,
                HostPort = agvDto.Port,
                VehicleWidth = agvDto.VehicleWidth,
                VehicleLength = agvDto.VehicleLength,
                Simulation = agvDto.Simulation,
                Protocol = agvDto.Protocol,
                InitTag = agvDto.InitTag,
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
        private static void HandleTaskDBChangeRequestRaising(object? sender, clsTaskDto task_data_dto)
        {
            WaitingForWriteToTaskDatabaseQueue.Enqueue(task_data_dto);
        }

        internal static Dictionary<string, clsAGVStateDto> AGVStatueDtoStored = new Dictionary<string, clsAGVStateDto>();
        private static async Task AGVStatesStoreWorker()
        {
            AGVSDatabase databse = new AGVSDatabase();
            while (true)
            {
                try
                {
                    clsAGVStateDto CreateDTO(IAGV agv)
                    {
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
                        };
                        return dto;
                    };
                    foreach (var agv in AllAGV)
                    {
                        var entity = CreateDTO(agv);
                        if (!AGVStatueDtoStored.ContainsKey(entity.AGV_Name))
                            AGVStatueDtoStored.Add(entity.AGV_Name, entity);
                        if (AGVStatueDtoStored[entity.AGV_Name].HasChanged(entity))
                        {
                            var dbentity = databse.tables.AgvStates.FirstOrDefault(ent => ent.AGV_Name == entity.AGV_Name);
                            if (dbentity != null)
                            {

                                dbentity.Update(entity);
                                await databse.SaveChanges();
                            }

                        }
                        else
                        {
                        }
                        AGVStatueDtoStored[entity.AGV_Name] = entity;
                    }

                }
                catch (Exception ex)
                {
                    LOG.ERROR($"AGVStatesStoreWorker 收集AGV狀態數據的過程中發生錯誤", ex);
                }

                await Task.Delay(50);
            }
        }

        private static void TaskAssignToAGVWorker()
        {
            Task.Factory.StartNew(async () =>
            {
                var database = new AGVSDatabase();
                while (true)
                {
                    Thread.Sleep(100);

                    foreach (var _agv in VMSManager.AllAGV)
                    {
                        if (_agv.taskDispatchModule == null)
                            continue;

                        var tasks = database.tables.Tasks.Where(_task => (_task.State == TASK_RUN_STATUS.WAIT || _task.State == TASK_RUN_STATUS.NAVIGATING) && _task.DesignatedAGVName == _agv.Name).AsNoTracking();
                        _agv.taskDispatchModule.taskList = tasks.ToList();
                    }
                }
            });
        }

        private static async Task TaskDatabaseChangeWorker()
        {
            await Task.Delay(100);
            var database = new AGVSDatabase();
            while (true)
            {
                await Task.Delay(100);
                try
                {
                    if (WaitingForWriteToTaskDatabaseQueue.Count > 0)
                    {
                        if (!WaitingForWriteToTaskDatabaseQueue.TryDequeue(out var dto))
                            continue;
                        var entity = database.tables.Tasks.FirstOrDefault(tk => tk.TaskName == dto.TaskName);
                        if (entity != null)
                        {
                            entity.Update(dto);
                            int save_cnt = await database.SaveChanges();
                            LOG.TRACE($"Database-Task Table Changed-Num={save_cnt}\r\n{dto.ToJson()}", false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await AlarmManagerCenter.AddAlarmAsync(ALARMS.ERROR_WHEN_TASK_STATUS_CHAGE_DB);
                }

            }
        }

        public static void Initialize()
        {
            ForkAGVVMS = new GPMForkAgvVMS();
        }

        internal static IAGV GetAGVByName(string AGVName)
        {
            return AllAGV.FirstOrDefault(agv => agv.Name == AGVName);
        }
        public static bool TryGetAGV(string AGVName, AGV_TYPE Model, out IAGV agv)
        {
            agv = null;
            var agvList = SearchAGVByName(AGVName, Model);

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


        public static List<IAGV> SearchAGVByName(string agv_name, AGV_TYPE model = AGV_TYPE.FORK)
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

        /// <summary>
        /// 找充電站,需符合以下條件:
        ///1. 沒有AGV佔據該充電站以及其二次定位點
        ///2. 充電站的狀態是IDLE(TODO)
        ///3. 沒有AGV準備要過去充電
        /// </summary>
        /// <param name="station"></param>
        /// <returns></returns>
        private static bool ChargeableMatch(MapPoint station)
        {
            if (!station.IsChargeAble())
                return false;

            ///1
            if (AllAGV.Any(agv => agv.states.Last_Visited_Node == station.TagNumber))
                return false;
            List<int> tagNumberOfStationSecondary = station.Target.Keys.Select(key => key).ToList(); //充電點的二次定位點tags
            ///1
            if (AllAGV.Any(agv => tagNumberOfStationSecondary.Contains(agv.states.Last_Visited_Node)))
                return false;
            ///3
            if (RunningAGVList.Any(agv => agv.taskDispatchModule.TaskStatusTracker.TaskOrder?.To_Station == station.TagNumber + ""))
                return false;

            return true;
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

            return await agv.Locating(localizationVM);
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
                    agv = new clsGPMForkAGV(dto.AGV_Name, option);
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
            bool addSuccess = await SaveVehicleInfoToDatabase(dto);
            return (addSuccess, addSuccess ? "" : "新增車輛失敗(修改資料庫失敗)");

        }

        internal static async Task<(bool confirm, string message)> EditVehicle(clsAGVStateDto dto, string ordAGVName)
        {

            using var agvdatabase = new AGVSDatabase();
            var databaseDto = agvdatabase.tables.AgvStates.FirstOrDefault(agv => agv.AGV_Name == ordAGVName);
            if (databaseDto != null)
            {
                var group = GetGroup(dto.Model);
                var oriAGV = AllAGV.FirstOrDefault(agv => agv.Name == ordAGVName);

                if (oriAGV == null)
                {
                    agvdatabase.tables.AgvStates.Add(dto);
                    return (true, "");
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

                await agvdatabase.SaveChanges();
            }
            else
            {
                agvdatabase.tables.AgvStates.Add(dto);
            }
            return (true, "");
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
    }
}

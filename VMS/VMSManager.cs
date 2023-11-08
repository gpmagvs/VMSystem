using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VMSystem.AGV;
using VMSystem.ViewModel;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using System.Diagnostics;
using AGVSystemCommonNet6.Log;
using static AGVSystemCommonNet6.clsEnums;
using AGVSystemCommonNet6.AGVDispatch;
using static AGVSystemCommonNet6.AGVDispatch.clsAGVSTcpServer;
using static VMSystem.AppSettings;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.Extensions.Options;
using static AGVSystemCommonNet6.Abstracts.CarComponent;
using System.Xml.Linq;

namespace VMSystem.VMS
{
    public partial class VMSManager
    {
        private const string Vehicle_Json_file = @"C:\AGVS\Vehicle.json";
        private static Dictionary<VMS_GROUP, VMSConfig> _vehicle_configs = new Dictionary<VMS_GROUP, VMSConfig>()
        {
            { VMS_GROUP.GPM_FORK , new VMSConfig
            {
                 AGV_List = new Dictionary<string, clsAGVOptions>
                 {
                     {"AGV_001", new clsAGVOptions
                     {
                          HostIP = "192.168.0.101",
                           HostPort=7025,
                            Enabled = true,
                             Simulation = false,
                              InitTag = 1,
                               Protocol =  clsAGVOptions.PROTOCOL.RESTFulAPI
                     } }
                 }
            } }
        };
        public static GPMForkAgvVMS ForkAGVVMS;
        public static Dictionary<VMS_GROUP, VMSAbstract> VMSList = new Dictionary<VMS_GROUP, VMSAbstract>();
        public static clsOptimizeAGVDispatcher OptimizeAGVDisaptchModule = new clsOptimizeAGVDispatcher();

        internal static List<IAGV> AllAGV
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

                return outputs.FindAll(agv => agv.options.Enabled);
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
            var _configs = ReadVMSVehicleGroupSetting();
            if (_configs != null)
            {
                _vehicle_configs = _configs;
            }

            foreach (var item in _vehicle_configs)
            {
                VMSAbstract VMSTeam = null;
                VMS_GROUP vms_type = item.Key;
                if (vms_type == VMS_GROUP.GPM_FORK)
                {
                    var gpm_for_agvList = item.Value.AGV_List.Select(kp => new clsGPMForkAGV(kp.Key, kp.Value)).ToList();
                    VMSTeam = new GPMForkAgvVMS(gpm_for_agvList);
                }
                else if (vms_type == VMS_GROUP.YUNTECH_FORK)
                {
                    var yuntech_fork_agvList = item.Value.AGV_List.Select(kp => new clsYunTechAGV(kp.Key, kp.Value)).ToList();
                    VMSTeam = new YunTechAgvVMS(yuntech_fork_agvList);
                }
                else if (vms_type == VMS_GROUP.GPM_INSPECTION_AGV)
                {
                    var gpm_inspection_agvList = item.Value.AGV_List.Select(kp => new clsGPMInspectionAGV(kp.Key, kp.Value)).ToList();
                    VMSTeam = new GPMInspectionAGVVMS(gpm_inspection_agvList);
                }
                VMSList.Add(item.Key, VMSTeam);
            }
            SaveVMSVehicleGroupSetting();
            TcpServer.OnClientConnected += TcpServer_OnClientConnected;
            Task.Factory.StartNew(async () =>
            {
                if (await TcpServer.Connect())
                {
                    LOG.INFO($"TCP/IP Server build done({TcpServer.IP}:{TcpServer.Port})");
                }
            });

            AGVStatesStoreWorker();

        }

        private static void AGVStatesStoreWorker()
        {

            Task.Run(async () =>
            {
                while (true)
                {
                    Thread.Sleep(100);
                    try
                    {
                        using (AGVStatusDBHelper dBHelper = new AGVStatusDBHelper())
                        {
                            clsAGVStateDto CreateDTO(IAGV agv)
                            {
                                return new clsAGVStateDto
                                {
                                    AGV_Name = agv.Name,
                                    Enabled = agv.options.Enabled,
                                    BatteryLevel_1 = agv.states.Electric_Volume[0],
                                    BatteryLevel_2 = agv.states.Electric_Volume.Length >= 2 ? agv.states.Electric_Volume[1] : -1,
                                    OnlineStatus = agv.online_state,
                                    MainStatus = agv.states.AGV_Status,
                                    CurrentCarrierID = agv.states.CSTID.Length == 0 ? "" : agv.states.CSTID[0],
                                    CurrentLocation = agv.states.Last_Visited_Node.ToString(),
                                    Theta = agv.states.Coordination.Theta,
                                    Connected = agv.connected,
                                    Group = agv.VMSGroup,
                                    Model = agv.model,
                                    TaskName = agv.main_state == MAIN_STATUS.RUN ? agv.taskDispatchModule.TaskStatusTracker.OrderTaskName : "",
                                    TaskRunStatus = agv.taskDispatchModule.TaskStatusTracker.TaskRunningStatus,
                                    TaskRunAction = agv.taskDispatchModule.TaskStatusTracker.TaskAction,
                                    CurrentAction = agv.taskDispatchModule.TaskStatusTracker.currentActionType
                                };
                            };
                            await dBHelper.Update(AllAGV.Select(agv => CreateDTO(agv)));
                            foreach (var item in AllAGV)
                            {
                                item.UpdateAGVStates(new RunningStatus
                                {
                                    Electric_Volume = item.states.Electric_Volume,
                                    AGV_Status = item.states.AGV_Status,
                                    Last_Visited_Node = item.states.Last_Visited_Node,
                                    Coordination = item.states.Coordination
                                });
                            }
                        }

                    }
                    catch (Exception ex)
                    {

                    }

                }
            });
        }

        private static Dictionary<VMS_GROUP, VMSConfig>? ReadVMSVehicleGroupSetting()
        {
            if (File.Exists(Vehicle_Json_file))
            {
                var json = File.ReadAllText(Vehicle_Json_file);
                if (json == null)
                {
                    return null;
                }
                return JsonConvert.DeserializeObject<Dictionary<VMS_GROUP, VMSConfig>>(json);
            }
            else
            {
                return null;
            }
        }
        private static void SaveVMSVehicleGroupSetting()
        {
            var _object = VMSList.ToDictionary(grop => grop.Key, grop => new { AGV_List = grop.Value.AGVList.ToDictionary(a => a.Key, a => a.Value.options) });
            File.WriteAllText(Vehicle_Json_file, JsonConvert.SerializeObject(_object, Formatting.Indented));

        }
        public static void Initialize()
        {
            ForkAGVVMS = new GPMForkAgvVMS();
        }

        public static bool TryGetAGV(string AGVName, AGV_MODEL Model, out IAGV agv)
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


        public static List<IAGV> SearchAGVByName(string agv_name, clsEnums.AGV_MODEL model = clsEnums.AGV_MODEL.FORK_AGV)
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
                        AlarmManagerCenter.AddAlarm(ALARMS.NO_AVAILABLE_CHARGE_PILE, level: ALARM_LEVEL.WARNING, taskName: taskData.TaskName);
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
    }
}

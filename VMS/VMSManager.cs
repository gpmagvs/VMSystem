using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.HttpHelper;
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

namespace VMSystem.VMS
{
    public class VMSManager
    {
        public static GPMForkAGVVMSEntity ForkAGVVMS;

        internal static List<IAGV> AllAGV
        {
            get
            {
                List<IAGV> outputs = new List<IAGV>();
                outputs.AddRange(ForkAGVVMS.AGVList.Values.ToArray());
                return outputs;
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

        internal static void Initialize(ConfigurationManager configuration)
        {

            Dictionary<string, object> settings = AppSettingsHelper.GetAppsettings();
            var agvlistDict = JsonConvert.DeserializeObject<Dictionary<string, clsConnections>>(settings["AGV_List"].ToString());
            var agvList = agvlistDict.Select(kp => (IAGV)new clsGPMForkAGV(kp.Key, kp.Value)).ToList();
            ForkAGVVMS = new GPMForkAGVVMSEntity(agvList);

        }

        public static void Initialize()
        {
            ForkAGVVMS = new GPMForkAGVVMSEntity();
        }

        public static IAGV SearchAGVByName(string agv_name)
        {
            if (AllAGV.Count == 0)
                return null;
            return AllAGV.FirstOrDefault(agv => agv.Name == agv_name);
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
                if (CheckTaskDataValid(agv, ref taskData))
                {
                    //agv.taskDispatchModule.AddTask(taskData);
                    return true;
                }
                else
                    return false;
            }
            else
            {
                return false;
            }
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
            //
            if (action == ACTION_TYPE.Charge)
            {
                if (agv.main_state == clsEnums.MAIN_STATUS.Charging)
                {
                    return true;
                }
                if (ToStationTag == -1)
                {

                    List<MapStation> chargeableStations = StaMap.GetChargeableStations();
                    chargeableStations = chargeableStations.FindAll(sta => ChargeableMatch(sta));
                    //先不考慮交通問題 挑一個最近的
                    chargeableStations = chargeableStations.OrderBy(st => st.CalculateDistance(agv.states.Corrdination.X, agv.states.Corrdination.Y)).ToList();
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
        private static bool ChargeableMatch(MapStation station)
        {
            if (!station.IsChargeAble())
                return false;

            ///1
            if (AllAGV.Any(agv => agv.states.Last_Visited_Node == station.TagNumber))
                return false;
            List<int> tagNumberOfStationSecondary = station.Target.Keys.Select(key => int.Parse(key)).ToList(); //充電點的二次定位點tags
            ///1
            if (AllAGV.Any(agv => tagNumberOfStationSecondary.Contains(agv.states.Last_Visited_Node)))
                return false;
            ///3
            if (RunningAGVList.Any(agv => agv.taskDispatchModule.ExecutingTask?.To_Station == station.TagNumber + ""))
                return false;

            return true;
        }

        private static bool CheckStationTypeMatch(ACTION_TYPE action, int FromStationTag, int ToStationTag)
        {
            MapStation from_station = StaMap.Map.Points.Values.FirstOrDefault(st => st.TagNumber == FromStationTag);
            MapStation to_station = StaMap.Map.Points.Values.FirstOrDefault(st => st.TagNumber == ToStationTag);

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

        internal static int TaskFeedback(FeedbackData feedbackData)
        {
            var busyAgvs = AllAGV.FindAll(agv => agv.taskDispatchModule.ExecutingTask != null);
            IAGV? agv = busyAgvs.FirstOrDefault(agv => agv.taskDispatchModule.ExecutingTask.TaskName == feedbackData.TaskName);
            if (agv != null)
            {
                return agv.taskDispatchModule.TaskFeedback(feedbackData);
            }
            else
            {
                return 4012;
            }
        }


    }
}

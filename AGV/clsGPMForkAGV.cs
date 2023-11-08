using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Alarm.VMS_ALARM;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices;
using AGVSystemCommonNet6.TASK;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RosSharp.RosBridgeClient;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using VMSystem.AGV.TaskDispatch;
using VMSystem.VMS;
using static AGVSystemCommonNet6.Abstracts.CarComponent;
using static AGVSystemCommonNet6.clsEnums;
using static VMSystem.AGV.clsAGVTaskDisaptchModule;

namespace VMSystem.AGV
{
    public class clsGPMForkAGV : IAGV
    {
        public clsGPMForkAGV(string name, clsAGVOptions options)
        {
            availabilityHelper = new AvailabilityHelper(name);
            this.options = options;
            Name = name;
            taskDispatchModule = new clsAGVTaskDisaptchModule(this);
            RestoreStatesFromDatabase();
            AutoParkWorker();
            AliveCheck();
            AGVHttp = new HttpHelper($"http://{options.HostIP}:{options.HostPort}");
            LOG.TRACE($"IAGV-{Name} Created, [vehicle length={options.VehicleLength} cm]");
        }


        public virtual clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_FORK;

        public bool simulationMode => options.Simulation;
        public string Name { get; set; }
        public virtual clsEnums.AGV_MODEL model { get; set; } = clsEnums.AGV_MODEL.FORK_AGV;
        private bool _connected = false;
        public DateTime lastTimeAliveCheckTime = DateTime.MinValue;

        public AvailabilityHelper availabilityHelper { get; private set; }
        private clsRunningStatus _states = new clsRunningStatus();
        public clsRunningStatus states
        {
            get => _states;
            set
            {
                currentMapPoint = StaMap.GetPointByTagNumber(value.Last_Visited_Node);
                AlarmCodes = value.Alarm_Code;
                _states = value;
            }
        }


        public Map map { get; set; }

        public AGVStatusDBHelper AGVStatusDBHelper { get; } = new AGVStatusDBHelper();
        public List<clsTaskDto> taskList { get; } = new List<clsTaskDto>();
        public IAGVTaskDispather taskDispatchModule { get; set; }
        public clsAGVOptions options { get; set; }

        /// <summary>
        /// 當前任務規劃移動軌跡
        /// </summary>
        public clsMapPoint[] CurrentTrajectory => taskDispatchModule.CurrentTrajectory;


        public bool connected
        {
            get => _connected;
            set
            {
                if (value)
                    lastTimeAliveCheckTime = DateTime.Now;

                if (_connected != value)
                {
                    bool reconnected = !_connected && value;
                    _connected = value;
                    if (!_connected)
                    {
                        online_mode_req = online_state = ONLINE_STATE.OFFLINE;
                    }
                }
            }
        }


        public ONLINE_STATE online_mode_req { get; set; } = ONLINE_STATE.OFFLINE;

        public clsEnums.ONLINE_STATE _online_state;
        public clsEnums.ONLINE_STATE online_state
        {
            get => _online_state;
            set
            {
                if (_online_state != value)
                {
                    _online_state = value;
                    if (main_state == clsEnums.MAIN_STATUS.IDLE)
                    {
                        availabilityHelper.ResetIDLEStartTime();
                    }
                }
            }
        }
        public clsEnums.MAIN_STATUS main_state
        {
            get
            {
                if ((int)states.AGV_Status == 0)
                    return clsEnums.MAIN_STATUS.IDLE;
                return states.AGV_Status;
            }
        }
        public MapPoint previousMapPoint { get; private set; } = null;
        public MapPoint currentMapPoint
        {
            get => previousMapPoint;
            set
            {

                if (previousMapPoint == null)
                {

                    StaMap.RegistPoint(Name, value, out string Registerrmsg);
                    previousMapPoint = value;
                    return;
                }
                if (previousMapPoint.TagNumber != value.TagNumber)
                {
                    if (previousMapPoint != null)
                    {
                        StaMap.UnRegistPoint(Name, previousMapPoint, out string UnRegisterrmsg);
                    }

                    StaMap.RegistPoint(Name, value, out string Registerrmsg);
                    previousMapPoint = value;
                }
            }
        }

        public List<clsAlarmDto> previousAlarmCodes = new List<clsAlarmDto>();
        public AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode[] AlarmCodes
        {
            set
            {
                if (value.Length == 0 && previousAlarmCodes.Count != 0)
                {
                    var previousUnCheckdeAlarms = AlarmManagerCenter.uncheckedAlarms.FindAll(alarm => alarm.Equipment_Name == Name);
                    AlarmManagerCenter.SetAlarmsAllCheckedByEquipmentName(Name);
                    previousAlarmCodes.Clear();
                }
                if (value.Length > 0)
                {
                    int[] newAlarmodes = value.Where(al => al.Alarm_ID != 0).Select(alarm => alarm.Alarm_ID).ToArray();
                    int[] _previousAlarmCodes = previousAlarmCodes.Select(alarm => alarm.AlarmCode).ToArray();

                    if (newAlarmodes.Length > 0)
                    {
                        foreach (int alarm_code in _previousAlarmCodes) //舊的
                        {
                            if (!newAlarmodes.Contains(alarm_code))
                            {
                                AlarmManagerCenter.SetAlarmChecked(Name, alarm_code);
                                previousAlarmCodes.RemoveAt(_previousAlarmCodes.ToList().IndexOf(alarm_code));
                            }
                        }
                    }

                    foreach (AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode alarm in value)
                    {
                        if (!_previousAlarmCodes.Contains(alarm.Alarm_ID)) //New Aalrm!
                        {

                            var alarmDto = new clsAlarmDto
                            {
                                AlarmCode = alarm.Alarm_ID,
                                Level = alarm.Alarm_Level == 1 ? ALARM_LEVEL.ALARM : ALARM_LEVEL.WARNING,
                                Description_En = alarm.Alarm_Description_EN,
                                Description_Zh = alarm.Alarm_Description,
                                Equipment_Name = Name,
                                Checked = false,
                                OccurLocation = currentMapPoint.Name,
                                Time = DateTime.Now,
                                Task_Name = taskDispatchModule.ExecutingTaskName,
                                Source = ALARM_SOURCE.EQP,

                            };
                            AlarmManagerCenter.AddAlarm(alarmDto);
                            previousAlarmCodes.Add(alarmDto);
                        }
                        else
                        {
                            AlarmManagerCenter.UpdateAlarmDuration(Name, alarm.Alarm_ID);
                        }
                    }

                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public List<int> NavigatingTagPath
        {
            get
            {
                return taskDispatchModule.TaskStatusTracker.RemainTags;
            }
        }

        public HttpHelper AGVHttp { get; set; }

        private bool _IsTrafficTaskExecuting = false;
        public bool IsTrafficTaskExecuting
        {
            get => _IsTrafficTaskExecuting;
            set
            {
                _IsTrafficTaskExecuting = value;
                IsTrafficTaskFinish = !value;
            }
        }
        public bool IsTrafficTaskFinish { get; set; } = false;

        private void AliveCheck()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    Thread.Sleep(1);
                    try
                    {
                        if (simulationMode)
                        {
                            connected = true;
                            //availabilityHelper.UpdateAGVMainState(main_state);
                            continue;
                        }
                        if ((DateTime.Now - lastTimeAliveCheckTime).TotalSeconds > 10)
                        {
                            connected = false;
                        }
                    }
                    catch (Exception ex)
                    {

                    }
                }
            });
        }

        public async Task PublishTrafficDynamicData(clsDynamicTrafficState data)
        {
            try
            {
                if (options.Protocol == clsAGVOptions.PROTOCOL.RESTFulAPI)
                {
                    await AGVHttp.PostAsync<clsDynamicTrafficState, object>($"/api/TrafficState/DynamicTrafficState", data);
                }
            }
            catch (Exception ex)
            {
                LOG.Critical($"發送多車動態資訊至AGV- {Name} 失敗", ex);
            }

        }
        /// <summary>
        /// IDLE 一段時間後下發停車任務
        /// </summary>
        private void AutoParkWorker()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    Thread.Sleep(1000);

                    if (online_state == clsEnums.ONLINE_STATE.OFFLINE | SystemModes.RunMode != AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
                        continue;

                    if (states.Cargo_Status == 1)
                        continue;

                    if (currentMapPoint.IsParking)
                        continue;

                    if (availabilityHelper.IDLING_TIME > 30)
                    {
                        LOG.WARN($"{Name} IDLE時間超過設定秒數");
                        var taskData = new clsTaskDto
                        {
                            Action = ACTION_TYPE.Park,
                            Carrier_ID = " ",
                            TaskName = $"*park-{DateTime.Now.ToString("yyyyMMddHHmmssfff")}",
                            DesignatedAGVName = Name,
                            DispatcherName = "VMS",
                            State = TASK_RUN_STATUS.WAIT
                        };

                        TaskDatabaseHelper DatabaseHelper = new TaskDatabaseHelper();
                        DatabaseHelper.Add(taskData);
                        availabilityHelper.ResetIDLEStartTime();

                    }
                }
            });
        }

        public async void UpdateAGVStates(clsRunningStatus status)
        {
            this.states = status;
            availabilityHelper.UpdateAGVMainState(main_state);
        }

        public async Task<bool> SaveStateToDatabase(clsAGVStateDto dto)
        {
            try
            {
                var result = await AGVStatusDBHelper.Update(dto);
                StaMap.TryGetPointByTagNumber(states.Last_Visited_Node, out var point);
                currentMapPoint = point;
                return result.confirm;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SaveStateToDatabase Fail " + ex.Message);
                AlarmManagerCenter.AddAlarm(ALARMS.ERROR_WHEN_AGV_STATUS_WRITE_TO_DB, ALARM_SOURCE.EQP, ALARM_LEVEL.WARNING, Name);
                return false;
            }

        }


        /// <summary>
        /// 從資料庫取出資料
        /// </summary>
        /// <returns></returns>
        public virtual async Task<object> GetAGVStateFromDB()
        {
            try
            {
                this.states = await AGVHttp.GetAsync<clsRunningStatus>($"/api/AGV/RunningState");
                if (states.Last_Visited_Node != this.states.Last_Visited_Node)
                {
                    Console.WriteLine($"{Name}:Last Visited Node : {states.Last_Visited_Node}");
                }
                this.states = states;
                online_state = await GetOnlineState();


                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }


        private async Task<clsEnums.ONLINE_STATE> GetOnlineState()
        {
            var state_code = await AGVHttp.GetAsync<int>($"/api/AGV/OnlineState");
            return state_code == 1 ? clsEnums.ONLINE_STATE.ONLINE : clsEnums.ONLINE_STATE.OFFLINE;
        }

        public bool AGVOnlineFromAGVS(out string message)
        {
            message = string.Empty;
            if (!CheckAGVStateToOnline(IsAGVRaiseRequest: false, out message))
            {
                online_mode_req = ONLINE_STATE.OFFLINE;
                return false;
            }
            if (options.Protocol == clsAGVOptions.PROTOCOL.RESTFulAPI)
            {
                var resDto = AGVHttp.GetAsync<clsAPIRequestResult>($"/api/AGV/agv_online").Result;
                if (!resDto.Success)
                {
                    AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_STATE_ERROR, ALARM_SOURCE.AGVS);
                }
                else
                    online_state = ONLINE_STATE.ONLINE;
                message = resDto.Message;

                return resDto.Success;
            }
            else
            {
                online_mode_req = ONLINE_STATE.ONLINE;
                return true;
            }
        }

        public bool AGVOfflineFromAGVS(out string message)
        {
            message = string.Empty;

            if (options.Protocol == clsAGVOptions.PROTOCOL.RESTFulAPI)
            {
                var resDto = AGVHttp.GetAsync<clsAPIRequestResult>($"/api/AGV/agv_offline").Result;
                if (!resDto.Success)
                {
                    AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_STATE_ERROR, ALARM_SOURCE.AGVS);
                }
                else
                    online_state = ONLINE_STATE.OFFLINE;
                message = resDto.Message;
                return resDto.Success;
            }
            else
            {
                online_mode_req = ONLINE_STATE.OFFLINE;
                return true;
            }
        }
        public bool AGVOfflineFromAGV(out string message)
        {
            message = string.Empty;
            online_mode_req = online_state = clsEnums.ONLINE_STATE.OFFLINE;
            return true;
        }

        public bool AGVOnlineFromAGV(out string message)
        {
            message = string.Empty;
            if (!CheckAGVStateToOnline(IsAGVRaiseRequest: true, out message))
            {
                online_mode_req = online_state = ONLINE_STATE.OFFLINE;
                return false;
            }
            else
            {
                online_mode_req = online_state = ONLINE_STATE.ONLINE;
                return true;
            }


        }
        private bool CheckAGVStateToOnline(bool IsAGVRaiseRequest, out string message)
        {
            message = string.Empty;
            if (!IsAGVRaiseRequest && !connected) //由派車系統發起上線請求才需要檢查連線狀態
            {
                message = AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_DISCONNECT, ALARM_SOURCE.AGVS);
                return false;
            }
            if (simulationMode)
            {
                online_state = clsEnums.ONLINE_STATE.ONLINE;
                return true;
            }

            var currentTag = states.Last_Visited_Node;

            if (!StaMap.CheckTagExistOnMap(currentTag))
            {
                AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_LOCATION_IS_NOT_EXIST_ON_MAP, ALARM_SOURCE.AGVS);
                message = $"{Name}目前位置{currentTag}不存在於圖資，禁止上線";
                return false;
            }

            if (main_state != clsEnums.MAIN_STATUS.IDLE && main_state != clsEnums.MAIN_STATUS.Charging)
            {
                AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_STATE_ERROR, ALARM_SOURCE.AGVS);
                message = $"AGV當前狀態禁止上線({main_state})";
                return false;
            }
            return true;
        }
        public string AddNewAlarm(ALARMS alarm_enum, ALARM_SOURCE source = ALARM_SOURCE.EQP, ALARM_LEVEL Level = ALARM_LEVEL.WARNING)
        {
            AGVSystemCommonNet6.Alarm.clsAlarmCode alarm = AlarmManagerCenter.GetAlarmCode(alarm_enum);
            clsAlarmDto alarmSave = new clsAlarmDto()
            {
                Time = DateTime.Now,
                AlarmCode = (int)alarm.AlarmCode,
                Description_En = alarm.Description_En,
                Description_Zh = alarm.Description_Zh,
                Equipment_Name = Name,
                Level = Level,
                Source = source,

            };
            alarmSave.Task_Name = taskDispatchModule.TaskStatusTracker.OrderTaskName;
            AlarmManagerCenter.AddAlarm(alarmSave);
            return alarmSave.Description_Zh;
        }

        /// <summary>
        /// 計算導航到目的地的成本花費
        /// </summary>
        /// <param name="map"></param>
        /// <param name="toTag"></param>
        /// <returns></returns>
        public int CalculatePathCost(Map map, object toTag)
        {
            int _toTag = int.Parse(toTag.ToString());
            PathFinder pathFinder = new PathFinder();
            PathFinder.clsPathInfo path_plan_info = pathFinder.FindShortestPathByTagNumber(map.Points, states.Last_Visited_Node, _toTag);
            return (int)Math.Round(path_plan_info.total_travel_distance);
        }

        public void CheckAGVStatesBeforeDispatchTask(ACTION_TYPE action, MapPoint DestinePoint)
        {
            bool IsCheckAGVCargoStatus = AGVSConfigulator.SysConfigs.TaskControlConfigs.CheckAGVCargoStatusWhenLDULDAction;
            if (action == ACTION_TYPE.None && currentMapPoint.StationType != STATION_TYPE.Normal)
            {
                throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_MOVE_TASK_IN_WORKSTATION);
            }

            if (action == ACTION_TYPE.None && DestinePoint.StationType != STATION_TYPE.Normal)
            {
                throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_NORMAL_MOVE_TASK_WHEN_DESTINE_IS_WORKSTATION);
            }

            if (action == ACTION_TYPE.Load)//放貨任務
            {
                if (!DestinePoint.IsEquipment)
                {
                    throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_LOAD_TASK_TO_NOT_EQ_STATION);
                }

                LOG.INFO($"[{Name}]-Check cargo status  before dispatch Load Task= {states.Cargo_Status}");
                if (states.Cargo_Status == 0 && IsCheckAGVCargoStatus)
                {
                    throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_LOAD_TASK_WHEN_AGV_NO_CARGO);
                }

            }
            if (action == ACTION_TYPE.Unload)
            {
                LOG.INFO($"[{Name}]-Check cargo status before dispatch Unload Task= {states.Cargo_Status}");
                if (!DestinePoint.IsEquipment)
                {
                    throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_UNLOAD_TASK_TO_NOT_EQ_STATION);

                }
                if (states.Cargo_Status == 1 && IsCheckAGVCargoStatus)
                {
                    throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_UNLOAD_TASK_WHEN_AGV_HAS_CARGO);
                }
            }
            if (action == ACTION_TYPE.Charge && !DestinePoint.IsChargeAble())
            {
                throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_CHARGE_TASK_TO_NOT_CHARGABLE_STATION);

            }
        }

        private void RestoreStatesFromDatabase()
        {
            RestorePreviousAlarmsFromDatabase();
            RestorePreviousLocationFromDatabase();
        }
        private void RestorePreviousAlarmsFromDatabase()
        {
            previousAlarmCodes = AlarmManagerCenter.GetAlarmsByEqName(Name).Where(alarm => alarm.Checked == false).ToList();

        }
        private void RestorePreviousLocationFromDatabase()
        {
            if (simulationMode)
            {
                states.Last_Visited_Node = options.InitTag;
                states.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
            }
            else
            {
                var status = AGVStatusDBHelper.GetAGVStateByAGVName(Name);
                if (status == null)
                {

                }
                else
                {
                    int.TryParse(status.CurrentLocation, out int tag);
                    states.Last_Visited_Node = tag;
                    previousMapPoint = StaMap.GetPointByTagNumber(tag);
                    states.Coordination.X = previousMapPoint.X;
                    states.Coordination.Y = previousMapPoint.Y;
                    states.Coordination.Theta = previousMapPoint.Direction;
                    StaMap.RegistPoint(Name, previousMapPoint, out string msg);
                }
            }
        }

    }
}

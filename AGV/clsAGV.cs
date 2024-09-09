using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Microservices.VMS;
using AGVSystemCommonNet6.Notify;
using AGVSystemCommonNet6.StopRegion;
using NLog;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using VMSystem.AGV.TaskDispatch;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch.Regions;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using WebSocketSharp;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static VMSystem.AGV.clsGPMInspectionAGV;

namespace VMSystem.AGV
{
    public class clsAGV : IAGV
    {
        public NLog.Logger logger { get; set; }
        public TaskExecuteHelper TaskExecuter { get; set; }
        public clsAGVSimulation AgvSimulation { get; set; } = new clsAGVSimulation();
        public clsAGV()
        {

        }
        public clsAGV(string name, clsAGVOptions options)
        {
            this.options = options;
            IsStatusSyncFromThirdPartySystem = AGVSConfigulator.SysConfigs.BaseOnKGSWebAGVSystem;
            Name = name;
            TaskExecuter = new TaskExecuteHelper(this);
            logger = LogManager.GetLogger($"AGVLog/{name}");
            logger.Info($"AGV {name} Create. MODEL={model} ");
            NavigationState.logger = logger;
            NavigationState.Vehicle = this;

            //重置座標
            states.Last_Visited_Node = options.InitTag;
            MapPoint previousPt = StaMap.GetPointByTagNumber(options.InitTag);
            states.Coordination = new clsCoordination(previousPt.X, previousPt.Y, previousPt.Direction);
            this.states = states;
            if (options.Simulation)
            {
                online_state = ONLINE_STATE.ONLINE;
                TagSetup();
            }
        }

        private void TagSetup()
        {
            Task.Run(async () =>
            {
                await Task.Delay(1000);
                AgvSimulation.SetTag(options.InitTag);
            });
        }

        public VehicleNavigationState NavigationState { get; set; } = new VehicleNavigationState();
        public virtual clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_FORK;
        public int AgvID { get; set; } = 0;
        public bool simulationMode => options.Simulation;
        public string Name { get; set; }
        public virtual AGV_TYPE model { get; set; } = AGV_TYPE.FORK;
        private bool _connected = false;
        public DateTime lastTimeAliveCheckTime = DateTime.MinValue;

        public event EventHandler<int> OnMapPointChanged;
        public static event EventHandler<(IAGV agv, double currentMileage)> OnMileageChanged;
        public event EventHandler<string> OnTaskCancel;
        public event EventHandler OnAGVStatusDown;

        public IAGV.BATTERY_STATUS batteryStatus
        {
            get
            {
                var volumes = states.Electric_Volume;
                if (volumes == null || volumes.Length == 0)
                    return IAGV.BATTERY_STATUS.UNKNOWN;

                double avgVolume = volumes.Average();
                if (avgVolume < options.BatteryOptions.LowLevel)
                    return IAGV.BATTERY_STATUS.LOW;
                else if (avgVolume > options.BatteryOptions.HightLevel)
                    return IAGV.BATTERY_STATUS.HIGH;
                else
                {
                    if (avgVolume < options.BatteryOptions.MiddleLevel)
                        return IAGV.BATTERY_STATUS.MIDDLE_LOW;
                    else
                        return IAGV.BATTERY_STATUS.MIDDLE_HIGH;
                }
            }
        }

        public AvailabilityHelper availabilityHelper { get; private set; }
        public StopRegionHelper StopRegionHelper { get; private set; }
        private clsRunningStatus _states = new clsRunningStatus() { Odometry = -1 };
        public clsRunningStatus states
        {
            get => _states;
            set
            {
                if (currentMapPoint.TagNumber != value.Last_Visited_Node)
                {
                    MapRegion previousRegion = currentMapPoint.GetRegion();

                    currentMapPoint = StaMap.GetPointByTagNumber(value.Last_Visited_Node);

                    MapRegion currentRegion = currentMapPoint.GetRegion();

                    //if (currentRegion.Name == NavigationState.RegionControlState.NextToGoRegion.Name) //抵達預計前往的區域
                    //{
                    //    NavigationState.RegionControlState.NextToGoRegion = new MapRegion();
                    //}
                }

                if (value.Odometry != _states.Odometry)
                {
                    OnMileageChanged?.Invoke(this, (this, value.Odometry));
                }

                AlarmCodes = value.Alarm_Code;
                _states = value;
                main_state = value.AGV_Status;



            }
        }


        public Map map { get; set; }

        public IAGVTaskDispather taskDispatchModule { get; set; } = new clsAGVTaskDisaptchModule();
        public clsAGVOptions options { get; set; }

        public bool IsStatusSyncFromThirdPartySystem { get; set; } = false;
        private bool _pingSuccess = true;
        public bool pingSuccess
        {
            get => _pingSuccess;
            set
            {
                if (_pingSuccess != value)
                {
                    _pingSuccess = value;
                    if (!value)
                    {
                        logger.Warn($"Ping Fail({options.HostIP})");
                        string location = currentMapPoint == null ? states.Last_Visited_Node + "" : currentMapPoint.Name;
                        AlarmManagerCenter.AddAlarmAsync(ALARMS.VMSDisconnectwithVehicle, Equipment_Name: Name, location: location, level: ALARM_LEVEL.WARNING);
                    }
                    else
                    {
                        AlarmManagerCenter.SetAlarmCheckedAsync(Name, ALARMS.VMSDisconnectwithVehicle, "SystemAuto");
                    }
                }
            }
        }

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
        public virtual clsEnums.ONLINE_STATE online_state
        {
            get => _online_state;
            set
            {
                if (_online_state != value)
                {
                    _online_state = value;
                    NavigationState.StateReset();
                    if (value == ONLINE_STATE.ONLINE)
                    {
                        taskDispatchModule.OrderHandler.RunningTask = new MoveToDestineTask();
                    }
                    //taskDispatchModule.AsyncTaskQueueFromDatabase();
                    if (main_state == clsEnums.MAIN_STATUS.IDLE)
                    {
                        availabilityHelper.ResetIDLEStartTime();
                    }
                }
            }
        }
        public clsEnums.MAIN_STATUS _main_state = MAIN_STATUS.Unknown;
        public clsEnums.MAIN_STATUS main_state
        {
            get => _main_state;
            set
            {
                if (value != _main_state)
                {
                    if (value == MAIN_STATUS.DOWN)
                    {
                        OnAGVStatusDown?.Invoke(this, EventArgs.Empty);
                    }
                    availabilityHelper?.UpdateAGVMainState(value);
                    StopRegionHelper?.UpdateStopRegionData(value, states.Last_Visited_Node.ToString());
                    _main_state = value;
                }
            }
        }

        public List<MapPoint> noRegistedByConflicCheck { get; set; } = new List<MapPoint>();
        public List<MapPoint> RegistedByConflicCheck { get; set; } = new List<MapPoint>();
        public MapPoint previousMapPoint { get; private set; } = new MapPoint("", -1);
        public MapPoint currentMapPoint
        {
            get => previousMapPoint;
            set
            {
                try
                {
                    IAGV _thisAGV = this;
                    var currentCircleArea = value.GetCircleArea(ref _thisAGV);
                    if (previousMapPoint.TagNumber != value.TagNumber)
                    {
                        int previousTag = (int)(previousMapPoint?.TagNumber);
                        logger.Info($"[{Name}]-Tag Location Change to {value.TagNumber} (Previous : {previousTag})");
                        if (value.IsEquipment && !TrafficControlCenter.TrafficControlParameters.Basic.UnLockEntryPointWhenParkAtEquipment)
                        {
                            //NotifyServiceHelper.INFO($"AGV {Name} 抵達{value.Graph.Display},系統設置入口點({previousMapPoint.Graph.Display})不可解除註冊!");
                            StaMap.RegistPoint(Name, value, out string _Registerrmsg);
                            previousMapPoint = value;
                            return;
                        }

                        if (previousMapPoint != null)
                        {
                            bool _isPreviousPointTooNearCurrnetPoint = previousMapPoint.GetCircleArea(ref _thisAGV).IsIntersectionTo(currentCircleArea);
                            //if (_isPreviousPointTooNearCurrnetPoint && previousMapPoint.TagNumber != value.TagNumber)
                            //{
                            //    noRegistedByConflicCheck.Add(previousMapPoint);
                            //}
                            //else
                            Task.Run(async () =>
                            {
                                try
                                {

                                    await StaMap.UnRegistPoint(Name, previousTag);

                                    IEnumerable<int> registedTags = StaMap.RegistDictionary.Where(kp => kp.Value.RegisterAGVName == this.Name).Select(kp => kp.Key);
                                    IEnumerable<int> regitedButNotInNavigationTags = registedTags.Where(tag => !NavigationState.NextNavigtionPoints.GetTagCollection().Contains(tag));

                                    if (regitedButNotInNavigationTags.Any() && this.CurrentRunningTask().ActionType == ACTION_TYPE.None)
                                    {
                                        foreach (var tag in regitedButNotInNavigationTags)
                                        {
                                            await StaMap.UnRegistPoint(Name, tag);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.Error(ex);
                                }
                            });


                        }

                        StaMap.RegistPoint(Name, value, out string Registerrmsg);

                        TrafficControl.PartsAGVSHelper.RegistStationRequestToAGVS(new List<string>() { value.Graph.Display });


                        if (noRegistedByConflicCheck.Any())
                        {
                            var _farPoints = noRegistedByConflicCheck.Where(pt => !pt.GetCircleArea(ref _thisAGV).IsIntersectionTo(currentCircleArea)).ToList();
                            foreach (var item in _farPoints)
                            {
                                StaMap.UnRegistPoint(Name, item);
                                noRegistedByConflicCheck.Remove(item);
                            }
                        }
                        if (RegistedByConflicCheck.Any())
                        {
                            var _farPoints = RegistedByConflicCheck.Where(pt => !this.CurrentRunningTask().RealTimeOptimizePathSearchReuslt.Contains(pt))
                                                                    .Where(pt => !pt.GetCircleArea(ref _thisAGV, 0.5).IsIntersectionTo(currentCircleArea)).ToList();
                            foreach (var item in _farPoints)
                            {
                                StaMap.UnRegistPoint(Name, item);
                                RegistedByConflicCheck.Remove(item);
                            }
                        }

                        previousMapPoint = value;

                        RegionManager.UpdateRegion(this);
                        OnMapPointChanged?.Invoke(this, value.TagNumber);
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
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
                        Task.Factory.StartNew(async () =>
                        {
                            foreach (int alarm_code in _previousAlarmCodes) //舊的
                            {
                                if (!newAlarmodes.Contains(alarm_code))
                                {
                                    await AlarmManagerCenter.SetAlarmCheckedAsync(Name, alarm_code);
                                    previousAlarmCodes.RemoveAt(_previousAlarmCodes.ToList().IndexOf(alarm_code));
                                }
                            }
                        });
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
                            AlarmManagerCenter.AddAlarmAsync(alarmDto);
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
        public clsAGVSTcpServer.clsAGVSTcpClientHandler? TcpClientHandler { get; set; }
        public bool IsSolvingTrafficInterLock { get; set; } = false;

        public MapPoint[] PlanningNavigationMapPoints
        {
            get
            {
                clsMapPoint[] _taskTrajectory = taskDispatchModule.OrderHandler.RunningTask.TaskDonwloadToAGV.Trajectory;
                if (_taskTrajectory.Count() == 0)
                    return new MapPoint[0];
                else
                {

                    var tags = _taskTrajectory.Select(pt => pt.Point_ID).ToList();
                    //var _agv_current_tag = currentMapPoint.TagNumber;
                    //var _agv_loc_index = tags.IndexOf(_agv_current_tag);
                    //var _remainTags = tags.Where(tag => tags.IndexOf(tag) >= _agv_loc_index).ToList();
                    //var mapPoints = StaMap.Map.Points.Values;
                    MapPoint[] _remain_points = tags.Select(tag => StaMap.GetPointByTagNumber(tag)).ToArray();
                    return _remain_points;
                }
            }
        }
        public MapCircleArea AGVRotaionGeometry
        {
            get
            {
                return new MapCircleArea((float)(options.VehicleLength / 100f), (float)(options.VehicleWidth / 100f), new System.Drawing.PointF((float)currentMapPoint.X, (float)currentMapPoint.Y));
            }
        }

        public MapRectangle AGVRealTimeGeometery
        {
            get
            {
                return TrafficControl.Tools.CreateAGVRectangle(this);
            }
        }
        public MapRectangle AGVCurrentPointGeometery
        {
            get
            {
                double x = currentMapPoint.X;
                double y = currentMapPoint.Y;
                double theta = states.Coordination.Theta;
                double width = options.VehicleWidth / 100.0;
                double length = options.VehicleLength / 100.0;
                return Tools.CreateRectangle(x, y, theta, width, length);
            }
        }
        public int currentFloor { get; set; } = 1;

        public async Task Run()
        {
            availabilityHelper = new AvailabilityHelper(Name);
            StopRegionHelper = new StopRegionHelper(Name);
            RestoreStatesFromDatabase();
            CreateTaskDispatchModuleInstance();
            AliveCheck();
            Console.WriteLine($"[{Name}] Alive Check Process Start");
            PingCheck();
            Console.WriteLine($"[{Name}] Ping Process Start");

            if (options.Simulation)
            {
                AgvSimulation = new clsAGVSimulation((clsAGVTaskDisaptchModule)taskDispatchModule);
                AgvSimulation.StartSimulation();
                Console.WriteLine($"[{Name}] Simulation Start");
            }


            AGVHttp = new HttpHelper($"http://{options.HostIP}:{options.HostPort}");
            logger.Trace($"IAGV-{Name} Created, [vehicle length={options.VehicleLength} cm]");

            taskDispatchModule.Run();

            if (!IsStatusSyncFromThirdPartySystem)
                RaiseOffLineRequestWhenSystemStartAsync();
        }

        private void RaiseOffLineRequestWhenSystemStartAsync()
        {
            Task.Run(async () =>
            {
                while (!AGVOfflineFromAGVS(out string msg))
                {
                    await Task.Delay(1000);
                }
            });
        }

        protected virtual void CreateTaskDispatchModuleInstance()
        {
            taskDispatchModule = new clsAGVTaskDisaptchModule(this);
        }

        public async Task<bool> PingServer()
        {
            using Ping pingSender = new Ping();
            // 設定要 ping 的主機或 IP
            string address = options.HostIP;
            try
            {  // 創建PingOptions對象，並設置相關屬性
                PingOptions options = new PingOptions
                {
                    Ttl = 128,                // 生存時間，可以根據需求設置
                    DontFragment = false      // 是否允許分段，設為true表示不分段
                };

                PingReply reply = await pingSender.SendPingAsync(address, 3000, new byte[32], options);
                bool ping_success = reply.Status == IPStatus.Success;
                return ping_success;
            }
            catch (PingException ex)
            {
                Console.WriteLine($"Ping Error: {ex.Message}");
                return false;
            }
        }

        private async Task PingCheck()
        {
            while (true)
            {
                pingSuccess = await PingServer();
                await Task.Delay(1000);
            }
        }

        /// <summary>
        /// Alive Check Process for AGV Connection Status Check 
        /// </summary>
        private void AliveCheck()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1);
                    try
                    {
                        if (simulationMode)
                        {
                            connected = true;
                            //availabilityHelper.UpdateAGVMainState(main_state);
                            continue;
                        }
                        if ((DateTime.Now - lastTimeAliveCheckTime).TotalSeconds > (Debugger.IsAttached ? 60 : 20))
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

                    if (online_state == clsEnums.ONLINE_STATE.OFFLINE || SystemModes.RunMode != AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN)
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
            if (options.Simulation)
            {
                online_state = clsEnums.ONLINE_STATE.ONLINE;
                return true;
            }

            if (IsStatusSyncFromThirdPartySystem)
            {
                KGSWebAGVSystemAPI.Vehicle.AGVAPI.ONLINE(AgvID + "");
                return true;
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

            if (IsStatusSyncFromThirdPartySystem)
            {
                KGSWebAGVSystemAPI.Vehicle.AGVAPI.OFFLINE(this.AgvID + "");
                return true;
            }

            online_mode_req = ONLINE_STATE.OFFLINE;
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
            AlarmManagerCenter.AddAlarmAsync(alarmSave);
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
            PathFinder.clsPathInfo path_plan_info = pathFinder.FindShortestPathByTagNumber(map, states.Last_Visited_Node, _toTag);
            return (int)Math.Round(path_plan_info.total_travel_distance);
        }

        public void CheckAGVStatesBeforeDispatchTask(ACTION_TYPE action, MapPoint DestinePoint)
        {
            bool IsCheckAGVCargoStatus = TrafficControlCenter.TrafficControlParameters.Basic.CheckAGVCargoStatusWhenLDULDAction;
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

                logger.Info($"[{Name}]-Check cargo status  before dispatch Load Task= {states.Cargo_Status}");
                if (states.Cargo_Status == 0 && IsCheckAGVCargoStatus)
                {
                    throw new IlleagalTaskDispatchException(ALARMS.CANNOT_DISPATCH_LOAD_TASK_WHEN_AGV_NO_CARGO);
                }

            }
            if (action == ACTION_TYPE.Unload)
            {
                logger.Info($"[{Name}]-Check cargo status before dispatch Unload Task= {states.Cargo_Status}");
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
                clsAGVStateDto data = DatabaseCaches.Vehicle.VehicleStates.FirstOrDefault(agv => agv.AGV_Name == Name);
                if (data == null)
                    return;

                int.TryParse(data.CurrentLocation, out int tag);
                states.Last_Visited_Node = tag;
                previousMapPoint = StaMap.GetPointByTagNumber(tag);
                states.Coordination.X = previousMapPoint.X;
                states.Coordination.Y = previousMapPoint.Y;
                states.Coordination.Theta = previousMapPoint.Direction;
                StaMap.RegistPoint(Name, previousMapPoint, out string msg);
            }
        }

        public bool IsAGVIdlingAtChargeStationButBatteryLevelLow()
        {
            return currentMapPoint.IsCharge && batteryStatus <= IAGV.BATTERY_STATUS.MIDDLE_LOW && main_state == clsEnums.MAIN_STATUS.IDLE;
        }
        public bool IsAGVIdlingAtNormalPoint()
        {
            return main_state == MAIN_STATUS.IDLE && currentMapPoint.StationType == STATION_TYPE.Normal;
        }

        public bool IsAGVHasCargoOrHasCargoID()
        {
            return states.Cargo_Status == 1 || states.CSTID.Any(id => id != "");
        }

        public virtual Task<(bool confirm, string message)> Locating(clsLocalizationVM localizationVM)
        {
            throw new NotImplementedException();
        }

        public async Task<bool> SpeedRecovertRequest()
        {
            try
            {
                return await AGVHttp.GetAsync<bool>("/api/TrafficState/SpeedDown");
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public async Task<bool> SpeedSlowRequest()
        {
            try
            {
                return await AGVHttp.GetAsync<bool>("/api/TrafficState/SpeedRecovery");
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public virtual bool CheckOutOrderExecutableByBatteryStatusAndChargingStatus(ACTION_TYPE orderAction, out string message)
        {
            message = "";

            //一律接受充電任務
            if (orderAction == ACTION_TYPE.Charge || batteryStatus == IAGV.BATTERY_STATUS.HIGH)
                return true;
            //電池低電量與電量未知不可接收任務
            if (batteryStatus <= IAGV.BATTERY_STATUS.LOW)
            {

                message = batteryStatus == IAGV.BATTERY_STATUS.LOW ? "電量過低無法接收訂單任務" : "電量狀態未知無法接收訂單任務";
                return false;
            }

            //充電中:
            if (main_state == MAIN_STATUS.Charging)
            {
                bool chargingAndAboveMiddle = batteryStatus == IAGV.BATTERY_STATUS.MIDDLE_HIGH;
                message = chargingAndAboveMiddle ? "" : "充電中但當前電量仍無法接收訂單任務。";
                return chargingAndAboveMiddle;
            }
            else//非充電中
            {
                return true;
            }
        }

        public bool IsDirectionHorizontalTo(IAGV otherAGV)
        {
            double Agv1X = states.Coordination.X;
            double Agv1Y = states.Coordination.Y;
            double Agv1Theta = states.Coordination.Theta;
            double Agv1Width = options.VehicleWidth;
            double Agv1Length = options.VehicleLength;

            double Agv2X = otherAGV.states.Coordination.X;
            double Agv2Y = otherAGV.states.Coordination.Y;
            double Agv2Theta = otherAGV.states.Coordination.Theta;
            double Agv2Width = otherAGV.options.VehicleWidth;
            double Agv2Length = otherAGV.options.VehicleLength;

            // 確定角度差異，調整為介於0和180度之間
            double angleDifference = Math.Abs(Agv1Theta - Agv2Theta);
            angleDifference = angleDifference > 180 ? 360 - angleDifference : angleDifference;
            double distance = Math.Sqrt(Math.Pow(Agv1X - Agv2X, 2) + Math.Pow(Agv1Y - Agv2Y, 2));
            bool isHorizon = angleDifference < 5 && angleDifference > -5 ||
                   angleDifference > 175 && angleDifference <= 180;
            //Console.WriteLine($"Direction To {otherAGV.Name} is Horizon(水平) ? {isHorizon} ");
            return isHorizon;

        }

        public async Task CancelTaskAsync(string task_name, string reason)
        {
            (bool confirmed, string message) = await taskDispatchModule.OrderHandler.CancelOrder(task_name, reason);
            if (confirmed)
            {
                TaskBase currentTask = this.CurrentRunningTask();
                bool isTaskExecuting = currentTask.TaskName == task_name;
                OnTaskCancel?.Invoke(this, task_name);
                if (isTaskExecuting && currentTask.ActionType == ACTION_TYPE.None && !currentTask.IsTaskCanceled)
                    currentTask.CancelTask();

                currentTask.OnTaskDone += (sender, e) =>
                {
                    RemoveTask(task_name);
                    void RemoveTask(string task_name)
                    {
                        try
                        {
                            var taskDto = taskDispatchModule.taskList.FirstOrDefault(tk => tk.TaskName == task_name);
                            if (taskDto != null)
                            {
                                taskDto.State = TASK_RUN_STATUS.CANCEL;
                                VMSManager.HandleTaskDBChangeRequestRaising(this, taskDto);
                            }
                            taskDispatchModule.taskList.RemoveAll(task => task.TaskName == task_name);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex);
                        }
                    }
                };


            }
        }

        private void CurrentTask_OnTaskDone(object? sender, EventArgs e)
        {
            throw new NotImplementedException();
        }
    }
}

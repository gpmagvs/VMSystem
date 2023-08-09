using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices;
using AGVSystemCommonNet6.TASK;
using Newtonsoft.Json;
using System.Diagnostics;
using VMSystem.VMS;

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
            if (simulationMode)
            {
                states.Last_Visited_Node = options.InitTag;
                states.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
            }
            if (!AGVStatusDBHelper.IsExist(name))
                SaveStateToDatabase();

            AutoParkWorker();
            AliveCheck();

        }
        public virtual clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_FORK;

        public bool simulationMode => options.Simulation;
        public string Name { get; set; }
        public virtual clsEnums.AGV_MODEL model { get; set; } = clsEnums.AGV_MODEL.FORK_AGV;
        private bool _connected = false;
        public DateTime lastTimeAliveCheckTime = DateTime.MinValue;

        /// <summary>
        /// 當前任務規劃移動軌跡
        /// </summary>
        public clsMapPoint[] CurrentTrajectory => taskDispatchModule.CurrentTrajectory;

        /// <summary>
        /// 剩餘的移動軌跡
        /// </summary>
        public clsMapPoint[] RemainTrajectory
        {
            get
            {
                if (CurrentTrajectory == null)
                    return new clsMapPoint[0];
                if (CurrentTrajectory.Length is 0)
                    return new clsMapPoint[0];
                var trajectoryList = CurrentTrajectory.ToList();
                var pt = trajectoryList.First(pt => pt.Point_ID == currentMapPoint.TagNumber);
                var index = trajectoryList.IndexOf(pt);
                clsMapPoint[] remainTrajectory = new clsMapPoint[trajectoryList.Count - index];
                CurrentTrajectory.ToList().CopyTo(index, remainTrajectory, 0, remainTrajectory.Length);
                return remainTrajectory;
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
                    _connected = value;
                    Task.Run(() => AGVStatusDBHelper.UpdateConnected(Name, value));

                }
            }
        }


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
                return states.AGV_Status;
            }
        }

        public MapPoint currentMapPoint
        {
            get
            {
                StaMap.TryGetPointByTagNumber(states.Last_Visited_Node, out var point);
                if (point == null)
                    return new MapPoint()
                    {
                        TagNumber = -1,
                        Name = "Unknown"
                    };
                return point;
            }
        }
        public AvailabilityHelper availabilityHelper { get; private set; }
        public RunningStatus states { get; set; } = new RunningStatus();
        public Map map { get; set; }

        public AGVStatusDBHelper AGVStatusDBHelper { get; } = new AGVStatusDBHelper();
        public List<clsTaskDto> taskList { get; } = new List<clsTaskDto>();
        public IAGVTaskDispather taskDispatchModule { get; set; }
        public clsAGVOptions options { get; set; }

        internal string HttpHost => $"http://{options.HostIP}:{options.HostPort}";

        /// <summary>
        /// 
        /// </summary>
        public List<int> NavigatingTagPath
        {
            get
            {
                IEnumerable<int> tags = ((clsAGVTaskDisaptchModule)taskDispatchModule).jobs.SelectMany(job =>
                    job.ExecutingTrajecory.Select(st => st.Point_ID)
                );

                if (tags.Count() <= 0)
                    return new List<int>();
                int currentTagIndex = tags.ToList().IndexOf(states.Last_Visited_Node);
                if (currentTagIndex < 0)
                    return new List<int>();
                else
                {
                    return tags.ToList().GetRange(currentTagIndex, tags.Count() - currentTagIndex);
                }
            }
        }


        private void AliveCheck()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    Thread.Sleep(1);
                    try
                    {
                        if (simulationMode)
                        {
                            connected = true;
                            availabilityHelper.UpdateAGVMainState(main_state);
                            SaveStateToDatabase();
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
                    await Http.PostAsync<clsDynamicTrafficState, object>($"{HttpHost}/api/TrafficState/DynamicTrafficState", data);
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
                    bool IsRunMode = false;
                    Thread.Sleep(1000);
                    try
                    {
                        IsRunMode = await Http.GetAsync<bool>($"{AGVSConfigulator.SysConfigs.AGVSHost}/api/system/RunMode");
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (online_state == clsEnums.ONLINE_STATE.OFFLINE | !IsRunMode)
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

        public async void UpdateAGVStates(RunningStatus status)
        {
            this.states = status;
            availabilityHelper.UpdateAGVMainState(main_state);
            await SaveStateToDatabase();
        }
        public async Task<bool> SaveStateToDatabase(clsAGVStateDto dto)
        {
            try
            {

                await Task.Delay(1);
                var result = await AGVStatusDBHelper.Update(dto);
                return result.confirm;
            }
            catch (Exception ex)
            {
                AlarmManagerCenter.AddAlarm(ALARMS.ERROR_WHEN_AGV_STATUS_WRITE_TO_DB, ALARM_SOURCE.EQP, ALARM_LEVEL.WARNING, Name);
                return false;
            }

        }


        public async Task<bool> SaveStateToDatabase()
        {
            try
            {
                await Task.Delay(1);
                var dto = new clsAGVStateDto()
                {
                    AGV_Name = Name,
                    Enabled = options.Enabled,
                    BatteryLevel = states.Electric_Volume.Length == 0 ? 0 : states.Electric_Volume[0],
                    OnlineStatus = online_state,
                    MainStatus = states.AGV_Status,
                    CurrentCarrierID = states.CSTID.Length == 0 ? "" : states.CSTID[0],
                    CurrentLocation = states.Last_Visited_Node.ToString(),
                    Theta = states.Coordination.Theta,
                    Connected = connected,
                    Group = VMSGroup,
                    Model = model,
                    TaskName = taskDispatchModule.ExecutingTask == null ? "" : taskDispatchModule.ExecutingTask.TaskName,
                    TaskRunStatus = taskDispatchModule.ExecutingTask == null ? TASK_RUN_STATUS.NO_MISSION : taskDispatchModule.ExecutingTask.State,
                    TaskRunAction = taskDispatchModule.ExecutingTask == null ? ACTION_TYPE.None : taskDispatchModule.ExecutingTask.Action,
                };
                return await SaveStateToDatabase(dto);
            }
            catch (Exception ex)
            {
                throw ex;
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
                this.states = await Http.GetAsync<RunningStatus>($"{HttpHost}/api/AGV/RunningState");
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
            var state_code = await Http.GetAsync<int>($"{HttpHost}/api/AGV/OnlineState");
            return state_code == 1 ? clsEnums.ONLINE_STATE.ONLINE : clsEnums.ONLINE_STATE.OFFLINE;
        }

        public bool Offline(out string message)
        {
            message = string.Empty;
            if (!connected)
            {
                message = AddNewAlarm(ALARMS.GET_OFFLINE_REQ_BUT_AGV_DISCONNECT, ALARM_SOURCE.AGVS);
                return false;
            }
            if (simulationMode)
            {
                online_state = clsEnums.ONLINE_STATE.OFFLINE;
                taskDispatchModule.CancelTask();
                return true;
            }

            taskDispatchModule.CancelTask();

            var resDto = Http.GetAsync<clsAPIRequestResult>($"{HttpHost}/api/AGV/agv_offline").Result;
            online_state = clsEnums.ONLINE_STATE.OFFLINE;

            SaveStateToDatabase();
            message = resDto.Message;
            return resDto.Success;
        }

        public bool Online(out string message)
        {
            message = string.Empty;
            if (!connected)
            {
                message = AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_DISCONNECT, ALARM_SOURCE.AGVS);
                return false;
            }
            if (simulationMode)
            {
                online_state = clsEnums.ONLINE_STATE.ONLINE;
                SaveStateToDatabase();
                return true;
            }
            var resDto = Http.GetAsync<clsAPIRequestResult>($"{HttpHost}/api/AGV/agv_online").Result;
            if (!resDto.Success)
            {
                AddNewAlarm(ALARMS.GET_ONLINE_REQ_BUT_AGV_STATE_ERROR, ALARM_SOURCE.AGVS);
            }
            else
                online_state = clsEnums.ONLINE_STATE.ONLINE;

            SaveStateToDatabase();
            message = resDto.Message;
            return resDto.Success;
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
            if (taskDispatchModule.ExecutingTask != null)
                alarmSave.Task_Name = taskDispatchModule.ExecutingTask.TaskName;
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


    }
}

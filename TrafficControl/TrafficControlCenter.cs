﻿using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.JSInterop.Infrastructure;
using System.Data.Common;
using AGVSystemCommonNet6.DATABASE;
using static AGVSystemCommonNet6.MAP.PathFinder;
using AGVSystemCommonNet6.AGVDispatch.Model;
using static AGVSystemCommonNet6.clsEnums;
using Newtonsoft.Json;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6;
using VMSystem.AGV.TaskDispatch;
using System.Collections.Concurrent;
using AGVSystemCommonNet6.AGVDispatch;

namespace VMSystem.TrafficControl
{
    public class TrafficControlCenter
    {
        public static ConcurrentQueue<clsTrafficInterLockSolver> InterLockTrafficSituations { get; set; } = new ConcurrentQueue<clsTrafficInterLockSolver>();
        internal static void Initialize()
        {
            SystemModes.OnRunModeON += HandleRunModeOn;
            clsWaitingInfo.OnAGVWaitingStatusChanged += ClsWaitingInfo_OnAGVWaitingStatusChanged;
            Task.Run(() => TrafficStateCollectorWorker());
            Task.Run(() => TrafficInterLockSolveWorker());
        }


        public static clsDynamicTrafficState DynamicTrafficState { get; set; } = new clsDynamicTrafficState();

        private static async void HandleRunModeOn()
        {
            var needGoToChargeAgvList = VMSManager.AllAGV.Where(agv => agv.currentMapPoint != null).Where(agv => !agv.currentMapPoint.IsCharge &&
                                                                        agv.main_state == MAIN_STATUS.IDLE &&
                                                                        agv.states.Cargo_Status == 0 &&
                                                                        agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.NO_ORDER)
                                                                        .ToList();

            foreach (var agv in needGoToChargeAgvList)
            {
                if (agv.states.Cargo_Status != 0)
                {
                    await AlarmManagerCenter.AddAlarmAsync(ALARMS.Cannot_Auto_Parking_When_AGV_Has_Cargo, level: ALARM_LEVEL.WARNING, Equipment_Name: agv.Name, location: agv.currentMapPoint.Name);
                    return;
                }
                using (TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper())
                {
                    TaskDBHelper.Add(new clsTaskDto
                    {
                        Action = ACTION_TYPE.Charge,
                        TaskName = $"Charge_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                        DispatcherName = "VMS",
                        DesignatedAGVName = agv.Name,
                        RecieveTime = DateTime.Now,
                    });
                }
                await Task.Delay(1000);
            }
        }

        private static void ClsWaitingInfo_OnAGVWaitingStatusChanged(clsWaitingInfo waitingInfo)
        {
            if (waitingInfo.IsWaiting)
            {
                LOG.INFO($"AGV-{waitingInfo.Agv.Name} waiting {waitingInfo.WaitingPoint.Name} passable.");
                //等待點由誰所註冊    
                var waitingForAGVName = waitingInfo.WaitingPoint.RegistInfo?.RegisterAGVName;
                IAGV agv_ = VMSManager.GetAGVByName(waitingForAGVName);
                if (agv_ != null)
                {
                    var waingInfoOfAgvRegistPt = agv_.taskDispatchModule.TaskStatusTracker.waitingInfo;
                    if (waingInfoOfAgvRegistPt.IsWaiting && waingInfoOfAgvRegistPt.WaitingPoint.RegistInfo?.RegisterAGVName == waitingInfo.Agv.Name)
                    {
                        //戶等
                        LOG.WARN($"Traffic Lock:{waitingInfo.Agv.Name}({waitingInfo.Agv.currentMapPoint.TagNumber}) and {agv_.Name}({agv_.currentMapPoint.TagNumber})  are waiting for each other");
                        clsTrafficInterLockSolver interlockSolver = new clsTrafficInterLockSolver(waitingInfo.Agv, agv_);
                        InterLockTrafficSituations.Enqueue(interlockSolver);
                    }
                }
                //var agv_conflic = VMSManager.GetAGVListExpectSpeficAGV(waitingInfo.Agv.Name).FirstOrDefault(agv => agv.taskDispatchModule.TaskStatusTracker.waitingInfo.WaitingPoint.TagNumber == agv.currentMapPoint.TagNumber)
            }
            else
                LOG.INFO($"AGV-{waitingInfo.Agv.Name} not waiting");
        }


        private static async void TrafficStateCollectorWorker()
        {
            while (true)
            {
                await Task.Delay(10);
                try
                {
                    DynamicTrafficState.AGVTrafficStates = VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv =>
                        new clsAGVTrafficState
                        {
                            AGVName = agv.Name,
                            CurrentPosition = StaMap.GetPointByTagNumber(agv.states.Last_Visited_Node),
                            AGVStatus = agv.main_state,
                            IsOnline = agv.online_state == ONLINE_STATE.ONLINE,
                            TaskRecieveTime = agv.main_state != MAIN_STATUS.RUN ? DateTime.MaxValue : agv.taskDispatchModule.TaskStatusTracker.TaskOrder == null ? DateTime.MaxValue : agv.taskDispatchModule.TaskStatusTracker.TaskOrder.RecieveTime,
                            PlanningNavTrajectory = agv.main_state != MAIN_STATUS.RUN ? new List<MapPoint>() : agv.taskDispatchModule.TaskStatusTracker.TaskOrder == null ? new List<MapPoint>() : agv.taskDispatchModule.CurrentTrajectory.ToList(),
                        }
                    );
                    DynamicTrafficState.RegistedPoints = StaMap.Map.Points.Values.ToList().FindAll(pt => pt.RegistInfo != null).FindAll(pt => pt.RegistInfo.IsRegisted);

                }
                catch (Exception ex)
                {

                }

            }

        }

        private static void TrafficInterLockSolveWorker()
        {
            while (true)
            {
                Thread.Sleep(1);
                try
                {
                    if (InterLockTrafficSituations.Count == 0) //No traffic problem to solve
                        continue;

                    if (InterLockTrafficSituations.TryDequeue(out var trafficProblemToSolve))
                    {
                        trafficProblemToSolve.StartSolve();
                    }

                }
                catch (Exception ex)
                {
                    LOG.Critical(ex);
                }
            }
        }

    }

    /// <summary>
    /// 解決交通互鎖的專家^_^
    /// </summary>
    public class clsTrafficInterLockSolver : clsTaskDatabaseWriteableAbstract
    {
        public clsTrafficInterLockSolver(IAGV Agv1, IAGV Agv2)
        {
            this.Agv1 = Agv1;
            this.Agv2 = Agv2;
            this.OccurTime = DateTime.Now;
        }
        public IAGV Agv1 { get; }
        public IAGV Agv2 { get; }


        private IAGV _Agv_Waiting;
        private IAGV _Agv_GoAway;

        public DateTime OccurTime { get; }
        public DateTime StartSolveTime { get; private set; }

        clsTaskDto tafTasOrder;
        internal async void StartSolve()
        {
            StartSolveTime = DateTime.Now;
            _Agv_GoAway = DetermineWhoIsNeedToGetOutAway(out _Agv_Waiting);
            Solve(_Agv_GoAway, _Agv_Waiting);
        }
        private async void Solve(IAGV AgvToGo, IAGV AgvWait)
        {
            bool confirmed = await RaiseAGVGoAwayRequest();
            if (confirmed)
            {
                string canceledTaskName = await AgvToGo.taskDispatchModule.CancelTask();
                LOG.INFO($"{AgvToGo.Name} cancel Task-{canceledTaskName},and AGV will release {AgvToGo.currentMapPoint.TagNumber}");
                bool TAFTaskStartOrFinish = await AwaitTAFTaskStart();
                if (TAFTaskStartOrFinish)
                {
                    SetCanceledTaskWait(canceledTaskName);
                }
            }
            else
            {
                //嘗試倒轉
                _Agv_GoAway = Agv1.Name == _Agv_Waiting.Name ? Agv1 : Agv2;
                _Agv_Waiting = Agv1.Name == _Agv_GoAway.Name ? Agv2 : Agv1;
                Solve(_Agv_GoAway, _Agv_Waiting);
            }
        }

        private async Task<bool> AwaitTAFTaskStart()
        {
            while (true)
            {
                Thread.Sleep(1);
                using var agvDB = new AGVSDatabase();
                var task = agvDB.tables.Tasks.FirstOrDefault(tk => tk.TaskName == tafTasOrder.TaskName);
                if (task == null)
                    continue;
                if (task.State == TASK_RUN_STATUS.NAVIGATING | task.State == TASK_RUN_STATUS.ACTION_FINISH)
                {
                    return true;
                }
                else if (task.State == TASK_RUN_STATUS.FAILURE | task.State == TASK_RUN_STATUS.CANCEL)
                {
                    return false;
                }
                else
                {
                    continue;
                }
            }
        }
        private void SetCanceledTaskWait(string canceledTaskName)
        {
            try
            {
                using (var db = new AGVSDatabase())
                {
                    var canceledTask = db.tables.Tasks.FirstOrDefault(t => t.TaskName == canceledTaskName);
                    canceledTask.State = TASK_RUN_STATUS.WAIT;
                    RaiseTaskDtoChange(this, canceledTask);
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }
        /// <summary>
        /// 決定哪一台AGV必須被趕走
        /// </summary>
        /// <returns></returns>
        private IAGV DetermineWhoIsNeedToGetOutAway(out IAGV AgvWaiting)
        {
            AgvWaiting = null;
            var agv1_start_wait_time = Agv1.taskDispatchModule.TaskStatusTracker.waitingInfo.StartWaitingTime;
            var agv2_start_wait_time = Agv1.taskDispatchModule.TaskStatusTracker.waitingInfo.StartWaitingTime;
            if (agv1_start_wait_time < agv2_start_wait_time)
            {
                AgvWaiting = Agv2;
                return Agv1;
            }
            else
            {
                AgvWaiting = Agv1;
                return Agv2;
            }
        }
        internal async Task<bool> RaiseAGVGoAwayRequest()
        {
            var pathes_of_waiting_agv = _Agv_Waiting.taskDispatchModule.TaskStatusTracker.SubTaskTracking.EntirePathPlan;
            var pathTags = pathes_of_waiting_agv.Select(pt => pt.TagNumber).ToList();
            MapPoint destinePt = pathes_of_waiting_agv.Last();

            LOG.WARN($"{_Agv_Waiting.Name} raise Fucking Stupid AGV Go Away(AGV Should Leave Tag{string.Join(",", pathTags)}) Rquest");


            TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();
            List<int> tagListAvoid = new List<int>();
            tagListAvoid.AddRange(VMSManager.AllAGV.Where(_agv => _agv.Name != _Agv_Waiting.Name).Select(_agv => _agv.states.Last_Visited_Node));
            tagListAvoid.AddRange(pathTags);

            var ptToParking = FindTagToParking(_Agv_GoAway.states.Last_Visited_Node, tagListAvoid, _Agv_GoAway.options.VehicleLength / 100.0);
            if (ptToParking == -1)
            {
                LOG.TRACE($"[Traffic] {_Agv_Waiting.Name} Request {_Agv_GoAway.Name} Far away from {_Agv_GoAway.currentMapPoint.TagNumber} but no tag can park.");
                return false;
            }
            LOG.TRACE($"[Traffic] Request {_Agv_GoAway.Name} Go to Tag {ptToParking}");
            tafTasOrder = new clsTaskDto
            {
                Action = ACTION_TYPE.None,
                TaskName = $"TAF_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                DesignatedAGVName = _Agv_GoAway.Name,
                Carrier_ID = "",
                DispatcherName = "Traffic",
                RecieveTime = DateTime.Now,
                Priority = 10,
                To_Station = ptToParking.ToString()
            };
            await TaskDBHelper.Add(tafTasOrder);
            return true;
        }
        private int FindTagToParking(int startTag, List<int> avoidTagList, double vechiel_length)
        {
            List<int> _avoidTagList = new List<int>();
            _avoidTagList.Add(startTag);
            _avoidTagList.AddRange(avoidTagList);
            var startPT = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == startTag);
            var ptAvaliable = StaMap.Map.Points.Values.Where(pt => pt.StationType == STATION_TYPE.Normal && !pt.IsVirtualPoint && pt.Enable).ToList().FindAll(pt => !_avoidTagList.Contains(pt.TagNumber));
            //計算出所有路徑
            PathFinder pf = new PathFinder();
            List<clsPathInfo> pfResultCollection = new List<clsPathInfo>();
            foreach (var validPT in ptAvaliable)
            {
                var pfResult = pf.FindShortestPath(StaMap.Map, startPT, validPT, new PathFinderOption { OnlyNormalPoint = true });
                if (pfResult != null)
                    pfResultCollection.Add(pfResult);
            }
            //依據走行總距離由小至大排序
            pfResultCollection = pfResultCollection.Where(pf => pf.waitPoints.Count == 0).OrderBy(pf => pf.total_travel_distance).ToList();
            if (pfResultCollection == null)
                return -1;
            var otherAgvTags = VMSManager.GetAGVListExpectSpeficAGV(_Agv_GoAway).Select(agv => agv.currentMapPoint.TagNumber).ToList();
            if (pfResultCollection.Count > 0)
            {
                //過濾出不會與[要求趕車之AGV]路徑衝突的路徑
                pfResultCollection = pfResultCollection.Where(pf => pf.tags.Any(tag => !_avoidTagList.Contains(tag))).ToList();
                var _avoidMapPoints = StaMap.Map.Points.Values.ToList().FindAll(pt => _avoidTagList.Contains(pt.TagNumber));

                pfResultCollection = pfResultCollection.Where(pf => _avoidMapPoints.All(pt => pt.CalculateDistance(pf.stations.Last()) >= vechiel_length)).ToList();
                pfResultCollection = pfResultCollection.Where(pf => pf.tags.All(tag => !otherAgvTags.Contains(tag))).ToList();
                if (pfResultCollection.Count == 0)
                    return -1;
                return pfResultCollection.First().stations.Last().TagNumber;
            }
            else
                return -1;
        }
    }
}

using AGVSystemCommonNet6.Alarm;
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
using AGVSystemCommonNet6.TASK;
using VMSystem.AGV.TaskDispatch;

namespace VMSystem.TrafficControl
{
    public class TrafficControlCenter
    {

        internal static void Initialize()
        {
            SystemModes.OnRunModeON += HandleRunModeOn;
            clsWaitingInfo.OnAGVWaitingStatusChanged += ClsWaitingInfo_OnAGVWaitingStatusChanged;
            Task.Run(() => TrafficStateCollectorWorker());
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
                LOG.INFO($"AGV-{waitingInfo.Agv.Name} waiting {waitingInfo.WaitingPoint.Name} passable.");
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

        internal static bool RaiseAGVGoAwayRequest(int tagNumber, List<MapPoint> OnRequestAGVNavingPath, string OnRequestAGVName, out List<IAGV> ExecutedAGVList)
        {
            ExecutedAGVList = new List<IAGV>();
            var pathTags = OnRequestAGVNavingPath.Select(pt => pt.TagNumber).ToList();
            pathTags = pathTags.FindAll(tag => pathTags.IndexOf(tag) > pathTags.IndexOf(tagNumber));
            MapPoint destinePt = OnRequestAGVNavingPath.Last();
            LOG.WARN($"{OnRequestAGVName} raise Fucking Stupid AGV Go Away(AGV Should Leave Tag{string.Join(",", pathTags)}) Rquest");

            //佔據導航路徑或與路徑任何一點干涉
            var agvListOfNeedGoway = VMSManager.AllAGV.Where(agv => agv.Name != OnRequestAGVName && pathTags.Contains(agv.states.Last_Visited_Node) | OnRequestAGVNavingPath.Any(pt => pt.CalculateDistance(agv.states.Coordination.X, agv.states.Coordination.Y) * 100.0 <= agv.options.VehicleLength));
            if (!agvListOfNeedGoway.Any())
                return false;

            List<int> parkingPtUsedList = new List<int>();//已經被booking的停車點位
            foreach (var agv in agvListOfNeedGoway)
            {
                Task _tfTask = new Task(() =>
                {
                    TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();
                    List<int> tagListAvoid = new List<int>();
                    tagListAvoid.Add(tagNumber);
                    tagListAvoid.AddRange(parkingPtUsedList);
                    tagListAvoid.AddRange(VMSManager.AllAGV.Where(_agv => _agv.Name != agv.Name).Select(_agv => _agv.states.Last_Visited_Node));
                    tagListAvoid.AddRange(pathTags);
                    var ptToParking = FindTagToParking(agv.states.Last_Visited_Node, tagListAvoid, agv.options.VehicleLength / 100.0);
                    if (ptToParking == -1)
                    {
                        LOG.TRACE($"[Traffic] {OnRequestAGVName} Request {agv.Name} Far away from {tagNumber} but no tag can park.");
                        return;
                    }
                    LOG.TRACE($"[Traffic] Request {agv.Name} Go to Tag {ptToParking}");
                    parkingPtUsedList.Add(ptToParking);
                    TaskDBHelper.Add(new AGVSystemCommonNet6.TASK.clsTaskDto
                    {
                        Action = ACTION_TYPE.None,
                        TaskName = $"TAF_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                        DesignatedAGVName = agv.Name,
                        Carrier_ID = "",
                        DispatcherName = "Traffic",
                        RecieveTime = DateTime.Now,
                        Priority = 10,
                        To_Station = ptToParking.ToString()
                    });
                });

                ExecutedAGVList.Add(agv);
                if (agv.main_state == MAIN_STATUS.RUN)
                {
                    Task.Factory.StartNew(async () =>
                    {
                        LOG.INFO($"Wait {agv.Name} Finish current Task TF Task Will Start");
                        while (agv.main_state == MAIN_STATUS.RUN)
                        {
                            await Task.Delay(1000);
                        }
                        _tfTask.Start();
                    });
                }
                else
                {
                    _tfTask.Start();
                }
            }
            return ExecutedAGVList.Count > 0;
        }
        private static int FindTagToParking(int startTag, List<int> avoidTagList, double vechiel_length)
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

            if (pfResultCollection.Count > 0)
            {
                //過濾出不會與[要求趕車之AGV]路徑衝突的路徑
                pfResultCollection = pfResultCollection.Where(pf => pf.tags.Any(tag => !_avoidTagList.Contains(tag))).ToList();
                var _avoidMapPoints = StaMap.Map.Points.Values.ToList().FindAll(pt => _avoidTagList.Contains(pt.TagNumber));

                pfResultCollection = pfResultCollection.Where(pf => _avoidMapPoints.All(pt => pt.CalculateDistance(pf.stations.Last()) >= vechiel_length)).ToList();

                return pfResultCollection.First().stations.Last().TagNumber;
            }
            else
                return -1;
        }
    }
}

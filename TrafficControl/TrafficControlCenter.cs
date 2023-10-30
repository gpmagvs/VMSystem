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

namespace VMSystem.TrafficControl
{
    public class TrafficControlCenter
    {

        internal static void Initialize()
        {
            SystemModes.OnRunModeON += HandleRunModeOn;
            Task.Run(() => TrafficStateCollectorWorker());
        }

        public static clsDynamicTrafficState DynamicTrafficState { get; set; } = new clsDynamicTrafficState();

        private static void HandleRunModeOn()
        {
            var needGoToChargeAgvList = VMSManager.AllAGV.Where(agv => !agv.currentMapPoint.IsCharge).ToList();
            foreach (var agv in needGoToChargeAgvList)
            {
                using (TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper())
                {
                    TaskDBHelper.Add(new clsTaskDto
                    {
                        Action = ACTION_TYPE.Charge,
                        TaskName = $"Charge_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                        DispatcherName = "VMS",
                        DesignatedAGVName = agv.Name,
                    });
                }
            }
        }

        private static async void TrafficStateCollectorWorker()
        {
            while (true)
            {
                await Task.Delay(10);
                try
                {
                    List<MapPoint> ConvertToMapPoint(clsMapPoint[] taskTrajecotry)
                    {
                        return taskTrajecotry.Select(pt => StaMap.GetPointByTagNumber(pt.Point_ID)).ToList();
                    };

                    DynamicTrafficState.AGVTrafficStates = VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv =>
                        new clsAGVTrafficState
                        {
                            AGVName = agv.Name,
                            CurrentPosition = StaMap.GetPointByTagNumber(agv.states.Last_Visited_Node),
                            AGVStatus = agv.main_state,
                            IsOnline = agv.online_state == ONLINE_STATE.ONLINE,
                            TaskRecieveTime = agv.main_state != MAIN_STATUS.RUN ? DateTime.MaxValue : agv.taskDispatchModule.TaskStatusTracker.TaskOrder == null ? DateTime.MaxValue : agv.taskDispatchModule.TaskStatusTracker.TaskOrder.RecieveTime,
                            PlanningNavTrajectory = agv.main_state != MAIN_STATUS.RUN ? new List<MapPoint>() : agv.taskDispatchModule.TaskStatusTracker.TaskOrder == null ? new List<MapPoint>() : ConvertToMapPoint(agv.taskDispatchModule.CurrentTrajectory),
                        }
                    );
                    DynamicTrafficState.RegistedPoints = StaMap.Map.Points.Values.ToList().FindAll(pt => pt.RegistInfo != null).FindAll(pt => pt.RegistInfo.IsRegisted);
                    //var sjon = JsonConvert.SerializeObject(DynamicTrafficState, Formatting.Indented);
                    //foreach (var agv in VMSManager.AllAGV)
                    //{
                    //    if (!agv.options.Simulation && agv.connected)
                    //        await agv.PublishTrafficDynamicData(DynamicTrafficState);
                    //}
                }
                catch (Exception ex)
                {

                }

            }

        }

        internal static void RaiseAGVGoAwayRequest(int tagNumber, List<MapPoint> OnRequestAGVNavingPath, string OnRequestAGVName = "")
        {
            var pathTags = OnRequestAGVNavingPath.Select(pt => pt.TagNumber).ToList();
            pathTags = pathTags.FindAll(tag => pathTags.IndexOf(tag) > pathTags.IndexOf(tagNumber));
            LOG.WARN($"{OnRequestAGVName} raise Fucking Stupid AGV Go Away(AGV Should Leave Tag{string.Join(",", pathTags)}) Rquest");

            var agvListOfNeedGoway = VMSManager.AllAGV.Where(agv => agv.Name != OnRequestAGVName && pathTags.Contains(agv.states.Last_Visited_Node));

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
                    var ptToParking = FindTagToParking(agv.states.Last_Visited_Node, tagListAvoid);
                    if (ptToParking == -1)
                        return;

                    parkingPtUsedList.Add(ptToParking);
                    TaskDBHelper.Add(new AGVSystemCommonNet6.TASK.clsTaskDto
                    {
                        Action = ACTION_TYPE.None,
                        TaskName = $"TAF_{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}",
                        DesignatedAGVName = agv.Name,
                        Carrier_ID = "",
                        DispatcherName = "Traffic",
                        RecieveTime = DateTime.Now,
                        Priority = 9,
                        To_Station = ptToParking.ToString()
                    });
                });

                if (agv.main_state == MAIN_STATUS.RUN)
                {
                    //continue;
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
                    _tfTask.Start();
            }

        }
        private static int FindTagToParking(int startTag, List<int> avoidTagList)
        {
            List<int> _avoidTagList = new List<int>();
            _avoidTagList.Add(startTag);
            _avoidTagList.AddRange(avoidTagList);
            var startPT = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber == startTag);
            var ptAvaliable = StaMap.Map.Points.Values.Where(pt => pt.StationType == STATION_TYPE.Normal && !pt.IsVirtualPoint && pt.Enable).ToList().FindAll(pt => !_avoidTagList.Contains(pt.TagNumber));
            ptAvaliable = ptAvaliable.OrderBy(pt => pt.CalculateDistance(startPT)).ToList();
            var pointFound = ptAvaliable.FirstOrDefault();
            if (pointFound != null)
            {
                return pointFound.TagNumber;
            }
            else
                return -1;
        }
    }
}

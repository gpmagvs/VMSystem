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

namespace VMSystem.TrafficControl
{
    public class TrafficControlCenter
    {

        internal static void Initialize()
        {
            //Task.Run(() => TrafficStateCollectorWorker());
        }


        /// <summary>
        /// 嘗試將AGV移動到避車點
        /// </summary>
        /// <param name="agv"></param>
        /// <param name="constrain_stations"></param>
        /// <returns></returns>
        internal static async Task<bool> TryMoveAGV(IAGV agv, List<MapPoint> constrain_stations)
        {
            var constrainTags = constrain_stations.Select(point => point.TagNumber);
            var otherAGVCurrentTag = VMSManager.AllAGV.FindAll(_agv => _agv != agv).Select(agv => agv.states.Last_Visited_Node);
            var move_goal_constrains = new List<int>();//最終停車的限制點
            move_goal_constrains.AddRange(constrainTags);
            move_goal_constrains.AddRange(otherAGVCurrentTag);
            var move_path_constrains = new List<int>();//停車過程行經路徑的的限制點
            move_path_constrains.AddRange(otherAGVCurrentTag);
            var avoid_pathinfos = FindAllAvoidPath(agv.states.Last_Visited_Node, move_goal_constrains, move_path_constrains);
            if (avoid_pathinfos.Count == 0)
                return false;
            var path = avoid_pathinfos.OrderBy(info => info.total_travel_distance).First();
            int avoid_tag = path.stations.Last().TagNumber;

            clsTaskDownloadData task_download_data = new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                Task_Name = "*TMC_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff"),
                CST = new clsCST[1] { new clsCST { CST_ID = "", CST_Type = 0 } },
                Destination = avoid_tag,
                Task_Sequence = 0,
                Escape_Flag = false,
                Station_Type = 0,
                Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, path.stations)
            };
            agv.taskDispatchModule.DispatchTrafficTask(task_download_data);
            // SimpleRequestResponse response = await agv.taskDispatchModule.PostTaskRequestToAGVAsync(task_download_data);
            return true;
        }

        public static List<clsPathInfo> FindAllAvoidPath(int startTag, List<int> goal_constrain_tag = null, List<int> path_contratin_tags = null)
        {
            var avoidStations = StaMap.GetAvoidStations();
            if (goal_constrain_tag != null)
                avoidStations = avoidStations.FindAll(pt => !goal_constrain_tag.Contains(pt.TagNumber));

            PathFinder pathFinder = new PathFinder();
            var option = new PathFinderOption()
            {
                ConstrainTags = path_contratin_tags == null ? new List<int>() : path_contratin_tags,
            };
            List<clsPathInfo> PathInfoList = new List<clsPathInfo>();
            foreach (var tag in avoidStations.Select(pt => pt.TagNumber))
            {
                try
                {
                    var pathInfo = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, startTag, tag, option);
                    if (pathInfo != null && pathInfo.total_travel_distance != 0)
                        PathInfoList.Add(pathInfo);
                }
                catch (Exception)
                {
                }
            }
            return PathInfoList;
        }

        public static List<clsPathInfo> FindAllParkPath(int startTag, List<int> goal_constrain_tag = null, List<int> path_contratin_tags = null)
        {
            var avoidStations = StaMap.GetParkableStations();
            if (goal_constrain_tag != null)
                avoidStations = avoidStations.FindAll(pt => !goal_constrain_tag.Contains(pt.TagNumber));

            PathFinder pathFinder = new PathFinder();
            var option = new PathFinderOption()
            {
                ConstrainTags = path_contratin_tags == null ? new List<int>() : path_contratin_tags,
            };
            List<clsPathInfo> PathInfoList = new List<clsPathInfo>();
            foreach (var tag in avoidStations.Select(pt => pt.TagNumber))
            {
                try
                {
                    var pathInfo = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, startTag, tag, option);
                    if (pathInfo != null && pathInfo.total_travel_distance != 0)
                        PathInfoList.Add(pathInfo);
                }
                catch (Exception)
                {
                }
            }
            return PathInfoList;
        }

        public static bool CalculatePathOverlaping(IEnumerable<int> path_1, IEnumerable<int> path_2, out List<int> overlap_tags)
        {
            var _path1 = path_1.ToHashSet<int>();
            var _path2 = path_2.ToHashSet<int>();
            overlap_tags = _path1.Intersect(path_2).ToList();
            return overlap_tags.Count > 1;
        }

        public static clsDynamicTrafficState DynamicTrafficState { get; set; } = new clsDynamicTrafficState();
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
                            TaskRecieveTime = agv.main_state != MAIN_STATUS.RUN ? DateTime.MaxValue : agv.taskDispatchModule.ExecutingTask == null ? DateTime.MaxValue : agv.taskDispatchModule.ExecutingTask.RecieveTime,
                            PlanningNavTrajectory = agv.main_state != MAIN_STATUS.RUN ? new List<MapPoint>() : agv.taskDispatchModule.ExecutingTask == null ? new List<MapPoint>() : ConvertToMapPoint(agv.taskDispatchModule.CurrentTrajectory),
                        }
                    );
                    DynamicTrafficState.RegistedPoints = StaMap.Map.Points.Values.ToList().FindAll(pt => pt.IsRegisted);
                    var sjon = JsonConvert.SerializeObject(DynamicTrafficState, Formatting.Indented);
                    foreach (var agv in VMSManager.AllAGV)
                    {
                        if (!agv.options.Simulation && agv.connected)
                            await agv.PublishTrafficDynamicData(DynamicTrafficState);
                    }
                }
                catch (Exception ex)
                {

                }

            }

        }
    }
}

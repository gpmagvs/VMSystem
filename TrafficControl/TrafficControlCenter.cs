using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.JSInterop.Infrastructure;
using System.Data.Common;
using AGVSystemCommonNet6.DATABASE;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.TrafficControl
{
    public class TrafficControlCenter
    {
        internal static clsTrafficState TrafficControlCheck(IAGV agv, clsMapPoint[] Trajectory)
        {
            if (Trajectory.Length == 0)
                return new clsTrafficState
                {
                    is_navigatable = true,
                };
            try
            {
                List<int> path_tags = Trajectory.Select(s => s.Point_ID).ToList();

                Console.WriteLine($"[交管中心] AGV{agv.Name}) 想要走 {string.Join("->", path_tags)}");
                var PathBlockedState = CheckAnyAgvOnPath(path_tags, agv);
                clsTrafficState TrafficState = null;

                if (PathBlockedState.blocked)
                {
                    clsMapPoint[] newTrajectory = TryCreatePathWithoutAGVBlocked(path_tags, agv);
                    bool hasOtherTrajectory = newTrajectory.Length > 0;

                    TrafficState = new clsTrafficState
                    {
                        is_path_replaned = hasOtherTrajectory,
                        blockedStations = hasOtherTrajectory ? null : PathBlockedState.blockedStations,
                        is_navigatable = hasOtherTrajectory,
                        path = path_tags,
                        request_agv = agv,
                        Trajectory = hasOtherTrajectory ? newTrajectory : Trajectory
                    };
                }
                else
                {
                    TrafficState = new clsTrafficState
                    {
                        blockedStations = PathBlockedState.blockedStations,
                        is_navigatable = !PathBlockedState.blocked,
                        path = path_tags,
                        request_agv = agv,
                        Trajectory = Trajectory
                    };
                }

                if (!TrafficState.is_navigatable)
                {
                    agv.AddNewAlarm(ALARMS.TRAFFIC_BLOCKED_NO_PATH_FOR_NAVIGATOR, ALARM_SOURCE.AGVS);
                    Console.WriteLine($"[交管中心] AGV{agv.Name}) 路徑無法行走。被{string.Join("、", TrafficState.blockedAgvNameList)} 阻擋");
                }
                return TrafficState;
            }
            catch (Exception ex)
            {
                agv.AddNewAlarm(ALARMS.TRAFFIC_CONTROL_CENTER_EXCEPTION_WHEN_CHECK_NAVIGATOR_PATH, ALARM_SOURCE.AGVS);
                throw ex;
            }

        }

        internal static (bool blocked, Dictionary<IAGV, int> blockedStations) CheckAnyAgvOnPath(List<int> path_tags, IAGV path_owner_agv)
        {

            List<IAGV> otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != path_owner_agv);
            var occupyStationAGVList = otherAGVList.FindAll(agv => path_tags.Contains(agv.states.Last_Visited_Node));
            if (occupyStationAGVList.Count > 0)
            {
                return (true, occupyStationAGVList.ToDictionary(agv => agv, agv => agv.states.Last_Visited_Node));
            }
            else
                return (false, null);
        }


        /// <summary>
        /// 計算路徑是否重複
        /// </summary>
        /// <returns></returns>
        internal static bool CalculatePathOverlaping(ref IAGV agv)
        {
            //計算最優路線
            //判斷是否有執行任務中的AGV >> 
            //  - 找到IDLE且擋路的AGV =>移車
            //  - 有執行中的AGV
            //     - 計算是否可跟車(路線重疊) ,
            //          - 可跟車? 跟!
            //          - 不可跟車
            //              - 繞路
            //              - 等待
            //     
            //
            return false;
        }

        /// <summary>
        /// 找一條沒有AGV阻擋的路
        /// </summary>
        internal static clsMapPoint[] TryCreatePathWithoutAGVBlocked(List<int> ori_path_tags, IAGV path_owner_agv)
        {
            List<IAGV> otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != path_owner_agv);
            int startTag = ori_path_tags.First();
            int endTag = ori_path_tags.Last();

            PathFinder pathFinder = new PathFinder();
            clsPathInfo pathFoundDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, startTag, endTag, new PathFinderOption
            {
                ConstrainTags = otherAGVList.Select(agv => agv.states.Last_Visited_Node).ToList(),
            });

            return PathFinder.GetTrajectory(StaMap.Map.Name, pathFoundDto.stations);

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
            //FindAllAvoidPath
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
            SimpleRequestResponse response = await agv.taskDispatchModule.PostTaskRequestToAGVAsync(task_download_data);
            return response.ReturnCode == RETURN_CODE.OK;
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

        public class clsTrafficState
        {
            public List<int> path { get; set; } = new List<int>();
            public IAGV request_agv { get; set; }
            public bool is_navigatable { get; set; }
            public bool is_path_replaned { get; set; }
            public Dictionary<IAGV, int> blockedStations { get; set; } = new Dictionary<IAGV, int>();

            public List<string> blockedAgvNameList => blockedStations.Select(kp => kp.Key.Name).ToList();

            public clsMapPoint[] Trajectory { get; set; }

        }
    }
}

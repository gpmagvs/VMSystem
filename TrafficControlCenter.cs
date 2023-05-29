using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem
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
        /// 找一條沒有AGV阻擋的路
        /// </summary>
        internal static clsMapPoint[] TryCreatePathWithoutAGVBlocked(List<int> ori_path_tags, IAGV path_owner_agv)
        {
            List<IAGV> otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != path_owner_agv);
            int startTag = ori_path_tags.First();
            int endTag = ori_path_tags.Last();

            PathFinder pathFinder = new PathFinder();
            PathFinder.clsPathInfo pathFoundDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, startTag, endTag, new PathFinder.PathFinderOption
            {
                AvoidTagNumbers = otherAGVList.Select(agv => agv.states.Last_Visited_Node).ToList(),
            });

            return pathFinder.GetTrajectory(StaMap.Map.Name, pathFoundDto.stations);

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

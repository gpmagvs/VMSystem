using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.AGV.TaskDispatch.Tasks.clsMoveTaskEvent;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.MapPoint;
using VMSystem.Extensions;

namespace VMSystem.TrafficControl.Solvers
{
    public class clsPathConflicSolver : ITrafficSolver
    {
        public IAGV ControledAGV { get; }
        public ITrafficSolver.TRAFFIC_SOLVER_SITUATION Situation { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public readonly clsMoveTaskEvent ReferenceTaskEvent;
        private ManualResetEvent WaitControledAGVReachCheckPoint = new ManualResetEvent(false);
        public IAGV RaiseControlAGV => ReferenceTaskEvent?.AGVRequestState.Agv;
        public clsPathConflicSolver(IAGV ControledAGV, clsMoveTaskEvent ReferenceTaskEvent)
        {
            this.ControledAGV = ControledAGV;
            this.ReferenceTaskEvent = ReferenceTaskEvent;
        }
        private Task WaitAGVMoveToGoalTask(IEnumerable<IAGV> _notBeObstacleAGVList)
        {
            return Task.Run(() =>
            {
                while (_notBeObstacleAGVList.Any(agv => agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING))
                {
                    Thread.Sleep(1);
                }
                string _agv_names = string.Join(",", _notBeObstacleAGVList.Select(agv => agv.Name));
                LOG.INFO($"{_agv_names} idling.");
            });
        }

        public async Task<clsSolverResult> Solve()
        {

            if (!IsAGVExecutingTask(ControledAGV))
            {
                await TryMoveBlockedAGV(this.ReferenceTaskEvent, ControledAGV);
            }
            else
            {
                WaitControledAGVReachCheckPoint.Reset();
                WaitControledAGVReachCheckPoint.WaitOne();
                ControledAGV.taskDispatchModule.OrderHandler.StartTrafficControl();
                await WaitAGVIDLE(ControledAGV);

                if (_TryFindPathPark(ControledAGV, ReferenceTaskEvent.AGVRequestState.OptimizedToDestineTrajectoryTagList, out var ParkPath))
                {
                    ControledAGV.taskDispatchModule.OrderHandler.RunningTask.Replan(ParkPath.tags);
                }
            }

            return new clsSolverResult()
            {

            };
        }

        private Task WaitAGVIDLE(IAGV controledAGV)
        {
            return Task.Run(() =>
            {
                while (controledAGV.main_state != MAIN_STATUS.IDLE)
                {
                    Thread.Sleep(1);
                }

            });
        }

        private async Task TryMoveBlockedAGV(clsMoveTaskEvent confirmArg, IAGV AgvToMove)
        {
            _WaitAGVIdle(AgvToMove);

            if (AgvToMove.main_state == MAIN_STATUS.DOWN)
                return;

            if (_TryFindPathPark(AgvToMove, confirmArg.AGVRequestState.OptimizedToDestineTrajectoryTagList, out var ParkPath))
            {
                int _toParkTag = ParkPath.tags.Last();
                using AGVSDatabase db = new AGVSDatabase();
                db.tables.Tasks.Add(new clsTaskDto
                {
                    Action = ACTION_TYPE.None,
                    To_Station = _toParkTag + "",
                    TaskName = $"TAF-{DateTime.Now.ToString("yyyyMMddHHmmssffff")}",
                    DesignatedAGVName = AgvToMove.Name,
                    IsTrafficControlTask = true
                });
                await db.SaveChanges();
                while (AgvToMove.states.Last_Visited_Node != _toParkTag)
                {
                    await Task.Delay(1);
                    if (AgvToMove.main_state == MAIN_STATUS.DOWN)
                        return;
                }
            }


            void _WaitAGVIdle(IAGV agv)
            {
                while (agv.main_state != MAIN_STATUS.IDLE)
                {
                    if (agv.main_state == MAIN_STATUS.DOWN)
                        break;
                    Thread.Sleep(1);
                }
            }
        }
        bool _TryFindPathPark(IAGV AgvToPark, List<int> WaitingAGVTrajTags, out clsPathInfo ParkPath)
        {
            ParkPath = null;
            List<int> OthersAGVCurrentTags = VMSManager.AllAGV.FilterOutAGVFromCollection(AgvToPark).Select(agv => agv.states.Last_Visited_Node).ToList();
            List<int> no_parkable_tag_list = new List<int>();
            no_parkable_tag_list.AddRange(WaitingAGVTrajTags);
            no_parkable_tag_list.AddRange(OthersAGVCurrentTags);
            no_parkable_tag_list = no_parkable_tag_list.Distinct().ToList();

            IEnumerable<MapPoint> normalPoints = StaMap.Map.Points.Values.Where(pt => pt.StationType == STATION_TYPE.Normal && pt.Enable && !pt.IsVirtualPoint);
            IEnumerable<MapPoint> parkablePoints = normalPoints.Where(pt => !no_parkable_tag_list.Contains(pt.TagNumber));

            clsPathInfo findPath(MapPoint AGVCurrentPoint, MapPoint ParkPoint, List<int> AviodPassTagList)
            {
                PathFinder pathFinder = new PathFinder();
                clsPathInfo _result = pathFinder.FindShortestPath(StaMap.Map, AGVCurrentPoint, ParkPoint, new PathFinderOption { ConstrainTags = AviodPassTagList });
                return _result;
            }

            var parkedPaths = parkablePoints.Select(pt => findPath(AgvToPark.currentMapPoint, pt, OthersAGVCurrentTags)).Where(result => result != null && result.stations.Count != 0).OrderBy(pathFindResult => pathFindResult.total_travel_distance);
            ParkPath = parkedPaths.FirstOrDefault();
            return ParkPath != null && ParkPath.stations.Count != 0;
        }

        private bool IsAGVExecutingTask(IAGV agv)
        {
            return agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
        }

        internal void NextActionStart()
        {
            WaitControledAGVReachCheckPoint.Set();
        }
    }
}

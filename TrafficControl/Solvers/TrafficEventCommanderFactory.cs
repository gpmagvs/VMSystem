using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using Microsoft.AspNetCore.Identity;
using VMSystem.AGV;
using static AGVSystemCommonNet6.MAP.PathFinder;
using VMSystem.VMS;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.AGV.TaskDispatch.Tasks;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;

namespace VMSystem.TrafficControl.Solvers
{

    public enum ETRAFFIC_EVENT
    {
        /// <summary>
        /// 趕車(讓路給另一輛車行駛)
        /// </summary>
        YieldWayToAnotherCar,
        /// <summary>
        /// 會車
        /// </summary>
        YieldForOncomingCar,
        /// <summary>
        /// 跟車
        /// </summary>
        FollowCar,
        /// <summary>
        /// 等待阻擋車輛抵達終點
        /// </summary>
        WaitYieldAGVReachDestine
    }

    public class TrafficEventCommanderFactory
    {
        public TrafficEventCommanderFactory() { }

        internal YieldWayEventCommander CreateYieldWayEventCommander(IAGV waitingForSolveAGV, IAGV conflicAGV)
        {
            YieldWayEventCommander commder = new YieldWayEventCommander(waitingForSolveAGV, conflicAGV);
            commder.CalculatePath();
            //commder.RegistPoint();
            return commder;
        }


        internal YieldWayEventWhenAGVMovingCommander CreateYieldWayEventWhenAGVMovingCommander(IAGV waitingForYieldAGV, IAGV yieldAGV)
        {
            YieldWayEventWhenAGVMovingCommander commder = new YieldWayEventWhenAGVMovingCommander(waitingForYieldAGV, yieldAGV);
            commder.CalculatePath();
            //commder.RegistPoint();
            return commder;
        }

        internal TrafficControlCommander CreateTrafficEventSolover(IAGV waitingForSolveAGV, IAGV conflicAGV)
        {
            TrafficControlCommander _solver;
            clsMoveTaskEvent moveTaskEventOfWaitingForSolveAGV = waitingForSolveAGV.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent;
            clsMoveTaskEvent moveTaskEventOfConflicAGV = conflicAGV.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent;

            bool IsConflicAGVExecutingTask = conflicAGV.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;

            if (!IsConflicAGVExecutingTask)
                _solver = new YieldWayEventCommander(waitingForSolveAGV, conflicAGV);
            else
            {
                bool IsConflicAGVBlockPathOfWaitingAGV = moveTaskEventOfWaitingForSolveAGV.AGVRequestState.RemainTagList.Contains(moveTaskEventOfConflicAGV.AGVRequestState.OptimizedToDestineTrajectoryTagList.Last());
                bool IsConflicAGVPathOverlapToWaitingAGV = moveTaskEventOfWaitingForSolveAGV.AGVRequestState.RemainTagList.Intersect(moveTaskEventOfConflicAGV.AGVRequestState.OptimizedToDestineTrajectoryTagList).Count() != 0;
                if (IsConflicAGVPathOverlapToWaitingAGV)
                {

                    _solver = new FollowCarEventCommander(waitingForSolveAGV, conflicAGV);
                }
                else
                    _solver = new YieldForOncomingCarEventCommander(waitingForSolveAGV, conflicAGV);
            }
            _solver.CalculatePath();
            //_solver.RegistPoint();
            return _solver;
        }
    }


    public abstract class TrafficControlCommander
    {
        public TrafficControlCommander(IAGV waitingForSolveAGV, IAGV conflicAGV)
        {
            this.waitingForSolveAGV = waitingForSolveAGV;
            this.conflicAGV = conflicAGV;
        }
        public readonly IAGV waitingForSolveAGV;
        public readonly IAGV conflicAGV;
        public abstract ETRAFFIC_EVENT TRAFFIC_EVENT { get; set; }
        public abstract PathFinder.clsPathInfo CalculatePath();
        public abstract void RegistPoint();
        public abstract Task<clsSolverResult> StartSolve();
        protected abstract void AssignVehicleRolesInTrafficEvent();
        protected void _WaitAGVIdle(IAGV agv)
        {
            while (agv.main_state != MAIN_STATUS.IDLE)
            {
                if (agv.main_state == MAIN_STATUS.DOWN)
                    break;
                Thread.Sleep(1);
            }
        }


    }
    /// <summary>
    /// [趕車] 事件指揮者
    /// </summary>
    public class YieldWayEventCommander : TrafficControlCommander
    {
        public clsMoveTaskEvent yieldingAGVMoveTaskState => yieldingAGV.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent;
        public clsMoveTaskEvent waitingAGVMoveTaskState => waitingForYieldAGV.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent;
        public YieldWayEventCommander(IAGV waitingForSolveAGV, IAGV conflicAGV) : base(waitingForSolveAGV, conflicAGV)
        {
            AssignVehicleRolesInTrafficEvent();
        }
        public override ETRAFFIC_EVENT TRAFFIC_EVENT { get; set; } = ETRAFFIC_EVENT.YieldWayToAnotherCar;
        public IAGV yieldingAGV;
        public IAGV waitingForYieldAGV;
        public PathFinder.clsPathInfo yieldPathInfo { get; protected set; }

        protected override void AssignVehicleRolesInTrafficEvent()
        {
            if (waitingForSolveAGV.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
            {
                waitingForYieldAGV = waitingForSolveAGV;
                yieldingAGV = conflicAGV;
            }
            else
            {
                yieldingAGV = waitingForSolveAGV;
                waitingForYieldAGV = conflicAGV;
            }
        }

        public override PathFinder.clsPathInfo CalculatePath()
        {
            List<int> waitingForYieldAGVTrajectoryTags = waitingForYieldAGV.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent.AGVRequestState.OptimizedToDestineTrajectoryTagList;
            _TryFindPathPark(this.yieldingAGV, waitingForYieldAGVTrajectoryTags, out clsPathInfo path);
            this.yieldPathInfo = path;
            return path;

        }

        public override async Task<clsSolverResult> StartSolve()
        {
            _WaitAGVIdle(this.yieldingAGV);

            if (yieldingAGV.main_state == MAIN_STATUS.DOWN)
                return new clsSolverResult();

            if (this.yieldPathInfo != null)
            {
                int _toParkTag = this.yieldPathInfo.tags.Last();
                await CreateYieldTask(_toParkTag);
                while (yieldingAGV.main_state != MAIN_STATUS.IDLE)
                {
                    await Task.Delay(1);
                    if (yieldingAGV.main_state == MAIN_STATUS.DOWN)
                        return new clsSolverResult();
                }

                return new clsSolverResult();
            }
            else
            {
                return new clsSolverResult();
            }


        }

        protected virtual async Task CreateYieldTask(int _toParkTag)
        {
            using AGVSDatabase db = new AGVSDatabase();
            db.tables.Tasks.Add(new clsTaskDto
            {
                Action = ACTION_TYPE.None,
                To_Station = _toParkTag + "",
                TaskName = $"TAF-{DateTime.Now.ToString("yyyyMMddHHmmssffff")}",
                DesignatedAGVName = yieldingAGV.Name,
                RecieveTime = DateTime.Now,
                IsTrafficControlTask = true
            });
            await db.SaveChanges();
        }

        protected bool _TryFindPathPark(IAGV yieldingAGV, List<int> waitingForYieldAGVTrajTags, out clsPathInfo ParkPath)
        {
            ParkPath = null;
            List<int> OthersAGVCurrentTags = VMSManager.AllAGV.FilterOutAGVFromCollection(yieldingAGV).Select(agv => agv.states.Last_Visited_Node).ToList();
            List<int> no_parkable_tag_list = new List<int>();
            no_parkable_tag_list.AddRange(waitingForYieldAGVTrajTags);
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

            var parkedPaths = parkablePoints.Select(pt => findPath(yieldingAGV.currentMapPoint, pt, OthersAGVCurrentTags)).Where(result => result != null && result.stations.Count != 0).OrderBy(pathFindResult => pathFindResult.total_travel_distance);
            ParkPath = parkedPaths.FirstOrDefault();
            return ParkPath != null && ParkPath.stations.Count != 0;
        }

        public override void RegistPoint()
        {
            StaMap.RegistPoint(yieldingAGV.Name, yieldPathInfo.tags, out string msg);
        }
    }

    /// <summary>
    /// 需要被趕車輛還在執行任務時，使用這個交通指揮者
    /// </summary>
    public class YieldWayEventWhenAGVMovingCommander : YieldWayEventCommander
    {
        public YieldWayEventWhenAGVMovingCommander(IAGV waitingForYieldAGV, IAGV yieldAGV) : base(waitingForYieldAGV, yieldAGV)
        {
        }
        protected override void AssignVehicleRolesInTrafficEvent()
        {
            waitingForYieldAGV = waitingForSolveAGV;
            yieldingAGV = conflicAGV;
        }
        public override async Task<clsSolverResult> StartSolve()
        {

            int _toParkTag = this.yieldPathInfo.tags.Last();
            await CreateYieldTask(_toParkTag);

            while (yieldingAGV.main_state != MAIN_STATUS.RUN)
            {
                LOG.TRACE($"Wait {this.yieldingAGV.Name} [Start] run yield move task..");
                await Task.Delay(1000);

                if (yieldingAGV.main_state == MAIN_STATUS.DOWN || _IsOrderFinish())
                    return new clsSolverResult(clsSolverResult.SOLVER_RESULT.FAIL);
            }
            LOG.TRACE($"{this.yieldingAGV.Name} start run yield move task..Wait finish");
            while (yieldingAGV.main_state != MAIN_STATUS.IDLE)
            {
                LOG.TRACE($"Wait {this.yieldingAGV.Name} [Finish] run yield move task..");
                await Task.Delay(1000);
                if (yieldingAGV.main_state == MAIN_STATUS.DOWN || _IsOrderFinish())
                    return new clsSolverResult(clsSolverResult.SOLVER_RESULT.FAIL);
            }

            bool _IsOrderFinish()
            {
                return yieldingAGV.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
            }

            StaMap.UnRegistPointsOfAGVRegisted(this.yieldingAGV);
            return new clsSolverResult(clsSolverResult.SOLVER_RESULT.SUCCESS);
        }
        protected override async Task CreateYieldTask(int _toParkTag)
        {
            CalculatePath();
            this.yieldingAGV.taskDispatchModule.OrderHandler.RunningTask.Replan(yieldPathInfo.tags);
        }
    }
    /// <summary>
    /// [會車] 事件指揮者
    /// </summary>
    public class YieldForOncomingCarEventCommander : TrafficControlCommander
    {
        public YieldForOncomingCarEventCommander(IAGV waitingForSolveAGV, IAGV conflicAGV) : base(waitingForSolveAGV, conflicAGV)
        {
        }

        public override ETRAFFIC_EVENT TRAFFIC_EVENT { get; set; } = ETRAFFIC_EVENT.YieldForOncomingCar;

        protected override void AssignVehicleRolesInTrafficEvent()
        {
            throw new NotImplementedException();
        }

        public override PathFinder.clsPathInfo CalculatePath()
        {
            throw new NotImplementedException();
        }

        public override Task<clsSolverResult> StartSolve()
        {
            throw new NotImplementedException();
        }

        public override void RegistPoint()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// [跟車] 事件指揮者
    /// </summary>
    public class FollowCarEventCommander : YieldWayEventCommander
    {
        public FollowCarEventCommander(IAGV waitingForSolveAGV, IAGV conflicAGV) : base(waitingForSolveAGV, conflicAGV)
        {
        }

        public override ETRAFFIC_EVENT TRAFFIC_EVENT { get; set; } = ETRAFFIC_EVENT.FollowCar;
        public List<int> OverlapTagList { get; protected set; } = new List<int>();

        public override clsPathInfo CalculatePath()
        {
            List<int> remainTagsOfConflicAGV = yieldingAGVMoveTaskState.AGVRequestState.RemainTagList;
            List<int> remainTagsOfWaitingAGV = waitingAGVMoveTaskState.AGVRequestState.RemainTagList;

            OverlapTagList = remainTagsOfWaitingAGV.Intersect(remainTagsOfConflicAGV).ToList();

            var yieldPathInfo = new clsPathInfo()
            {
                stations = OverlapTagList.Select(tag => StaMap.GetPointByTagNumber(tag)).ToList(),
            };
            this.yieldPathInfo = yieldPathInfo;
            return yieldPathInfo;
        }
        public override async Task<clsSolverResult> StartSolve()
        {
            try
            {
                MoveTask followAGVMoveTask = (MoveTask)waitingForYieldAGV.taskDispatchModule.OrderHandler.RunningTask;
                //MapPoint _destineMapPoint = StaMap.GetPointByTagNumber(waitingAGVMoveTaskState.OptimizedToDestineTrajectoryTagList.Last());
                //clsPathInfo path_found_result = new PathFinder().FindShortestPath(StaMap.Map, waitingForYieldAGV.currentMapPoint, _destineMapPoint);
                List<List<MapPoint>> checkPointsTaskSequenceList = GenSequenceTaskByOverlapPoints(waitingAGVMoveTaskState.AGVRequestState.OptimizedToDestineTrajectoryTagList, this.OverlapTagList);
                //foreach (var tag in OverlapTagList)
                //{
                //    checkPointsTaskSequenceList.FirstOrDefault(seq=>seq.)
                //}

                for (int i = 0; i < checkPointsTaskSequenceList.Count; i++)
                {
                    List<MapPoint> seq = checkPointsTaskSequenceList[i];
                    clsTaskDownloadData _taskDownload = followAGVMoveTask.TaskDonwloadToAGV.Clone();
                    _taskDownload.Trajectory = seq.Select(pt => TaskBase.MapPointToTaskPoint(pt)).ToArray();
                    int _agv_current_tag = waitingForYieldAGV.states.Last_Visited_Node;
                    List<int> seqTagList = seq.Select(mp => mp.TagNumber).ToList();

                    StaMap.RegistPoint(waitingForYieldAGV.Name, seqTagList.Skip(seqTagList.IndexOf(_agv_current_tag) + 1), out string error_message);
                    await followAGVMoveTask.SendTaskToAGV(_taskDownload);
                    bool _isNextCheckPointPassible = false;

                    int _lastTagPark = seqTagList.Last();
                    int _nextCheckPoint = checkPointsTaskSequenceList.Count - 1 == i ? seq.Last().TagNumber : checkPointsTaskSequenceList[i + 1].Last().TagNumber;
                    bool IsNextCheckPointPassiable(int tag)
                    {
                        return !StaMap.RegistDictionary.Keys.Contains(tag);
                    }
                    while (waitingForYieldAGV.states.Last_Visited_Node != seqTagList.Last() && !_isNextCheckPointPassible)
                    {
                        Thread.Sleep(1);
                        _isNextCheckPointPassible = IsNextCheckPointPassiable(_nextCheckPoint);
                    }
                }
                return new clsSolverResult();
            }
            catch (Exception ex)
            {

                throw ex;
            }

        }


        public virtual List<List<MapPoint>> GenSequenceTaskByOverlapPoints(List<int> optimized_trajectory_tags, List<int> overlap_tag)
        {

            List<List<MapPoint>> sequenceList = new List<List<MapPoint>>();

            List<MapPoint> optimized_traj_points = optimized_trajectory_tags.Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
            List<MapPoint> traffic_eheck_points = overlap_tag.Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
            if (traffic_eheck_points.Count == 0)
            {
                sequenceList.Add(optimized_traj_points);
                return sequenceList;
            }

            sequenceList = traffic_eheck_points.Select(point => optimized_traj_points.Take(optimized_trajectory_tags.FindIndex(mp => mp == point.TagNumber)).ToList()).ToList();
            if (!sequenceList.Last().SequenceEqual(optimized_traj_points))
                sequenceList.Add(optimized_traj_points);
            return sequenceList;
        }

        public override void RegistPoint()
        {
            //base.RegistPoint();
        }
    }

    public class WaitConflicAGVReachDesinteCommander : FollowCarEventCommander
    {
        public WaitConflicAGVReachDesinteCommander(IAGV waitingForSolveAGV, IAGV conflicAGV) : base(waitingForSolveAGV, conflicAGV)
        {
        }
    }
}

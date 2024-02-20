using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using RosSharp.RosBridgeClient.MessageTypes.Moveit;
using System.Diagnostics.Tracing;
using System.Net;
using VMSystem.Tools;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.AGV.TaskDispatch.IAGVTaskDispather;
using static VMSystem.AGV.TaskDispatch.Tasks.clsMoveTaskEvent;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public abstract class MoveTask : TaskBase
    {

        public List<List<MapPoint>> TaskSequenceList { get; private set; } = new List<List<MapPoint>>();
        protected MoveTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override ACTION_TYPE ActionType => ACTION_TYPE.None;

        public override void CreateTaskToAGV()
        {
            base.CreateTaskToAGV();

            MapPoint GetDesinteWorkStation()
            {
                if (this.Stage == VehicleMovementStage.Traveling_To_Source)
                {
                    return StaMap.GetPointByTagNumber(this.OrderData.From_Station_Tag);
                }
                else if (this.Stage == VehicleMovementStage.Traveling_To_Destine)
                {
                    return StaMap.GetPointByTagNumber(this.OrderData.To_Station_Tag);
                }
                else
                {
                    throw new Exception();
                }
            }
            MapPoint _destine_point = new MapPoint();

            if (OrderData.Action != ACTION_TYPE.None)
            {
                MapPoint _desintWorkStation = GetDesinteWorkStation();
                _destine_point = StaMap.GetPointByIndex(_desintWorkStation.Target.Keys.First());
            }
            else
            {
                _destine_point = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
            }
            clsPathInfo path_found_result = PathFind(_destine_point, new List<int>());
            this.TaskSequenceList = GenSequenceTaskByTrafficCheckPoints(path_found_result.stations);
            this.TaskDonwloadToAGV.Trajectory = path_found_result.stations.Select(pt => TaskBase.MapPointToTaskPoint(pt)).ToArray();
            this.TaskDonwloadToAGV.Destination = _destine_point.TagNumber;


            LOG.TRACE($"{this.TaskDonwloadToAGV.Task_Name}_ Path Sequences:\r\n" + string.Join("r\n", this.TaskSequenceList.Select(seq => string.Join("->", seq.GetTagList()))));
        }

        private clsPathInfo PathFind(MapPoint _destine_point, List<int> constrainTags)
        {
            PathFinder _pathinfder = new PathFinder();
            PathFinderOption pathFinderOption = new PathFinderOption()
            {
                ConstrainTags = constrainTags,
            };
            clsPathInfo path_found_result = _pathinfder.FindShortestPath(StaMap.Map, AGVCurrentMapPoint, _destine_point, pathFinderOption); //最優路徑

            if (path_found_result == null && constrainTags.Count > 0)
            {
                return _pathinfder.FindShortestPath(StaMap.Map, AGVCurrentMapPoint, _destine_point); //最優路徑
            }

            var constrainOfPath = GetConstrainTags(ref path_found_result);
            if (constrainOfPath.Count == 0)
                return path_found_result;
            constrainTags.AddRange(constrainOfPath);
            constrainTags = constrainTags.Distinct().ToList();
            return PathFind(_destine_point, constrainTags); //遞迴方式
        }

        private List<int> GetConstrainTags(ref clsPathInfo optimized_path_info)
        {
            List<int> tags = new List<int>();
            var registedPoints = StaMap.RegistDictionary.Where(kp => kp.Value.RegisterAGVName != Agv.Name).Select(kp => kp.Key).ToList();

            var _pointsRegisted = optimized_path_info.tags.Intersect(registedPoints);
            bool isAnyPointRegisted = _pointsRegisted.Count() != 0;

            if (isAnyPointRegisted)
                tags.AddRange(registedPoints);

            return tags.Distinct().ToList();
        }
        public override void CancelTask()
        {
            if (MoveTaskEvent != null)
            {
                MoveTaskEvent.TrafficResponse.ConfirmResult = clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.CANCEL;
                MoveTaskEvent.TrafficResponse.Wait_Traffic_Control_Finish_ResetEvent.Set();
            }

            base.CancelTask();
        }

        public override async Task SendTaskToAGV()
        {
            //base.SendTaskToAGV();
            var _OptimizedTrajectoryTags = TaskDonwloadToAGV.TagsOfTrajectory.ToArray();

            async Task _ExecuteSequenceTasks(List<List<MapPoint>> _taskSequenceList)
            {
                MoveTaskEvent = new clsMoveTaskEvent(Agv, _taskSequenceList, _OptimizedTrajectoryTags, _taskSequenceList[0], OrderData.IsTrafficControlTask);
                //await HandleTrafficControlCenterAction();
                try
                {
                    string task_simplex = this.TaskDonwloadToAGV.Task_Simplex;
                    foreach (List<MapPoint> stations in _taskSequenceList)
                    {
                        await Task.Delay(100);
                        if (MoveTaskEvent.TrafficResponse.ConfirmResult == GOTO_NEXT_GOAL_CONFIRM_RESULT.CANCEL)
                            break;

                        int _sequenceIndex = _taskSequenceList.IndexOf(stations);
                        clsTaskDownloadData _taskDownloadData = this.TaskDonwloadToAGV.Clone();
                        _taskDownloadData.Task_Sequence = _sequenceIndex;
                        _taskDownloadData.Task_Simplex = task_simplex + "-" + _sequenceIndex;
                        _taskDownloadData.Trajectory = stations.Select(pt => TaskBase.MapPointToTaskPoint(pt)).ToArray();
                        int _final_goal = stations.Last().TagNumber;

                        //計算停車角度
                        DetermineThetaOfDestine(_taskDownloadData);
                        TaskDonwloadToAGV = _taskDownloadData;
                        MoveTaskEvent = new clsMoveTaskEvent(Agv, _taskSequenceList, _OptimizedTrajectoryTags, stations, OrderData.IsTrafficControlTask);

                        if(TryGetOtherBetterPath(MoveTaskEvent ,out clsPathInfo newPathInfo))
                        {

                        }

                        await WaitNextPathRegistedOrConflicPointsCleared();

                        if (MoveTaskEvent.TrafficResponse.ConfirmResult == GOTO_NEXT_GOAL_CONFIRM_RESULT.CANCEL)
                        {
                            break;
                        }
                        if (MoveTaskEvent.TrafficResponse.ConfirmResult == GOTO_NEXT_GOAL_CONFIRM_RESULT.REPLAN)
                        {
                            List<List<MapPoint>> newTaskList;
                            CreateNewTaskSequenceList(out _OptimizedTrajectoryTags, out newTaskList);
                            await Task.Factory.StartNew(async () => await _ExecuteSequenceTasks(newTaskList));
                            return;
                        }

                        StaMap.RegistPoint(Agv.Name, MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList, out string ErrorMessage);
                        var _result = _DispatchTaskToAGV(_taskDownloadData, out var alarm);
                        if (_result.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                            throw new Exceptions.AGVRejectTaskException(_result.ReturnCode);
                        bool _agv_reach_goal = WaitAGVReachGoal(_final_goal);

                        if (MoveTaskEvent.TrafficResponse.ConfirmResult == GOTO_NEXT_GOAL_CONFIRM_RESULT.GO_TO_CHECK_POINT_AND_WAIT)
                        {
                            MoveTaskEvent.TrafficResponse.Wait_Traffic_Control_Finish_ResetEvent.WaitOne();
                        }
                        TrafficWaitingState.SetStatusNoWaiting();

                    }

                }
                catch (Exception ex)
                {
                    LOG.Critical(ex);
                    return;
                }
            }

            await _ExecuteSequenceTasks(this.TaskSequenceList);

        }

        private bool TryGetOtherBetterPath(clsMoveTaskEvent currentMoveEvent, out clsPathInfo newPathInfo)
        {
            var nextGoal = currentMoveEvent.AGVRequestState.NextSequenceTaskTrajectory.Last();
            newPathInfo = PathFind(nextGoal, new List<int>());
            bool hasNewPath = newPathInfo.tags.SequenceEqual(currentMoveEvent.AGVRequestState.NextSequenceTaskRemainTagList);
            return hasNewPath;
        }

        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            _taskDownloadData.Trajectory.Last().Theta = _Calculate_stop_angle(_taskDownloadData.Destination, OrderData.Action, _taskDownloadData.Trajectory);
        }

        private async Task HandleTrafficControlCenterAction()
        {
            if (BeforeMoveToNextGoalTaskDispatch != null)
            {
                MoveTaskEvent = await BeforeMoveToNextGoalTaskDispatch(MoveTaskEvent);

                if (MoveTaskEvent.TrafficResponse.YieldWayAGVList.Count != 0)
                {
                    Task WaitYieldWayActionStartAndReachGoal(IAGV yieldWayAGV)
                    {
                        return Task.Run(async () =>
                        {
                            await WaitOrderStart(yieldWayAGV);
                            await WaitReachGoal(yieldWayAGV);
                            #region region methods

                            async Task WaitOrderStart(IAGV yieldWayAGV)
                            {
                                while (yieldWayAGV.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING || yieldWayAGV.taskDispatchModule.OrderHandler.RunningTask.OrderData == null)
                                {
                                    await Task.Delay(1);
                                }
                            }
                            async Task WaitReachGoal(IAGV yieldWayAGV)
                            {
                                int destine = yieldWayAGV.taskDispatchModule.OrderHandler.RunningTask.OrderData.To_Station_Tag;
                                while (yieldWayAGV.states.Last_Visited_Node != destine)
                                {
                                    await Task.Delay(1);
                                }
                            }
                            #endregion
                        });
                    }
                    Task[] wait_yield_way_start_tasks = MoveTaskEvent.TrafficResponse.YieldWayAGVList.Select(agv => WaitYieldWayActionStartAndReachGoal(agv)).ToArray();
                    CancellationTokenSource wait_yield_way_task_done = new CancellationTokenSource();
                    Task MonitorRegistPointProcess = Task.Run(() =>
                    {
                        while (true)
                        {
                            Thread.Sleep(100);
                            if (wait_yield_way_task_done.IsCancellationRequested)
                                return;
                            List<int> yieldTaskRemainTags = MoveTaskEvent.TrafficResponse.YieldWayAGVList.Where(agv => agv.taskDispatchModule.OrderHandler.RunningTask.OrderData != null)
                                                                         .SelectMany(agv => agv.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent.AGVRequestState.RemainTagList)
                                                                         .Distinct().ToList();
                            var conflicTags = MoveTaskEvent.AGVRequestState.RemainTagList.Intersect(yieldTaskRemainTags).Reverse().ToList();
                            if (conflicTags.Count > 0)
                                TrafficWaitingState.SetStatusWaitingConflictPointRelease(conflicTags);
                        }
                    });
                    Task.WaitAll(wait_yield_way_start_tasks);
                    wait_yield_way_task_done.Cancel();
                }
            }
        }


        /// <summary>
        /// 等待註冊點解註冊或等待干涉點位無干涉
        /// </summary>
        /// <returns></returns>
        private async Task WaitNextPathRegistedOrConflicPointsCleared()
        {
            await Task.Delay(100);
            List<int> registedTags = new List<int>();
            List<IAGV> blockedTagAGVList = new List<IAGV>();
            UpdateRegistedTags(out registedTags);
            UpdateBlockedAGVList(registedTags, out blockedTagAGVList);
            IEnumerable<IAGV> conflicAgvList = new List<IAGV>();
            bool IsInterference = false;
            while (MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList.Any(tag => IsTagRegistedByOthers(tag)) || (IsInterference = _IsPathIntererce(out conflicAgvList)) == true)
            {
                if (MoveTaskEvent.TrafficResponse.ConfirmResult == GOTO_NEXT_GOAL_CONFIRM_RESULT.CANCEL)
                {
                    TrafficWaitingState.SetStatusNoWaiting();
                    return;
                }
                UpdateRegistedTags(out registedTags);
                UpdateBlockedAGVList(registedTags, out blockedTagAGVList);
                blockedTagAGVList.AddRange(conflicAgvList);
                blockedTagAGVList = blockedTagAGVList.Distinct().ToList();
                foreach (var AGV in blockedTagAGVList)
                {
                    if (AGV.taskDispatchModule.AGVWaitingYouNotify(this.Agv) == WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY.PLEASE_YIELD_ME)
                        break;
                }
                if (IsInterference)
                {
                    string _conflic_agv_names = string.Join(",", conflicAgvList.Select(AGV => AGV.Name).ToList());
                    TrafficWaitingState.SetStatusWaitingConflictPointRelease(registedTags, $"等待與 {_conflic_agv_names} 路徑干涉解除");
                }
                else
                    TrafficWaitingState.SetStatusWaitingConflictPointRelease(registedTags);
                await Task.Delay(100);
            }

            foreach (var AGV in blockedTagAGVList)
                AGV.taskDispatchModule.AGVNotWaitingYouNotify(this.Agv);

            TrafficWaitingState.SetStatusNoWaiting();


            bool IsTagRegistedByOthers(int tag)
            {
                bool registed = StaMap.RegistDictionary.TryGetValue(tag, out var result);
                if (!registed || result == null)
                    return false;
                return result.RegisterAGVName != this.Agv.Name;
            }
            void UpdateRegistedTags(out List<int> tags)
            {
                tags = MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList.Where(tag => IsTagRegistedByOthers(tag)).ToList();
            }
            void UpdateBlockedAGVList(List<int> registedTags, out List<IAGV> agvList)
            {
                agvList = registedTags.Select(tag => StaMap.RegistDictionary[tag].RegisterAGVName).Select(name => VMSManager.GetAGVByName(name)).Distinct().ToList();
            }

            bool _IsPathIntererce(out IEnumerable<IAGV> conflicAgvList)
            {
                int currentTagIndex = MoveTaskEvent.AGVRequestState.NextSequenceTaskTrajectory.GetTagList().ToList().IndexOf(this.Agv.states.Last_Visited_Node);
                var remainTrajectory = MoveTaskEvent.AGVRequestState.NextSequenceTaskTrajectory.Skip(currentTagIndex);
                return VMSystem.TrafficControl.Tools.CalculatePathInterference(remainTrajectory, this.Agv, VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv), out conflicAgvList);
            }
        }

        private void CreateNewTaskSequenceList(out int[] _OptimizedTrajectoryTags, out List<List<MapPoint>> newTaskList)
        {
            _OptimizedTrajectoryTags = MoveTaskEvent.TrafficResponse.NewTrajectory.Select(p => p.Point_ID).ToArray();
            List<MapPoint> newTrj = _OptimizedTrajectoryTags.Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
            newTaskList = GenSequenceTaskByTrafficCheckPoints(newTrj);
            var _index = newTaskList.FindIndex(seq => seq.Last().TagNumber == Agv.currentMapPoint.TagNumber);
            newTaskList = newTaskList.Skip(_index + 1).ToList();
        }

        /// <summary>
        /// 計算停車角度
        /// </summary>
        /// <param name="destinationTag"></param>
        /// <param name="order_action"></param>
        /// <param name="trajectory"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private double _Calculate_stop_angle(int destinationTag, ACTION_TYPE order_action, clsMapPoint[] trajectory)
        {
            if (trajectory.Length < 1)
                throw new Exception("計算停車角度需要至少1個路徑點位");
            clsMapPoint lastPoint = trajectory.Last();
            if (trajectory.Length == 1)
            {
                if (order_action != ACTION_TYPE.None)
                    return StaMap.GetPointByTagNumber(OrderData.To_Station_Tag).Direction_Secondary_Point;
                else
                    return lastPoint.Theta;
            }
            clsMapPoint second_lastPoint = trajectory[trajectory.Length - 2];
            if (lastPoint.Point_ID == destinationTag && order_action != ACTION_TYPE.None)
            {
                return StaMap.GetPointByTagNumber(OrderData.To_Station_Tag).Direction_Secondary_Point;
            }
            else
                return NavigationTools.CalculationForwardAngle(new System.Drawing.PointF((float)second_lastPoint.X, (float)second_lastPoint.Y),
                    new System.Drawing.PointF((float)lastPoint.X, (float)lastPoint.Y));
        }


        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            switch (confirmArg.TrafficResponse.ConfirmResult)
            {
                case clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.ACCEPTED_GOTO_NEXT_GOAL:
                    TrafficWaitingState.SetStatusNoWaiting();
                    break;
                case clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.WAIT_IN_CURRENT_LOCATION:

                    bool IsTagRegistedByOthers(int tag)
                    {
                        return StaMap.RegistDictionary.Where(kp => kp.Value.RegisterAGVName != Agv.Name).Select(kp => kp.Key).Contains(tag);
                    }
                    while (confirmArg.AGVRequestState.NextSequenceTaskRemainTagList.Any(tag => IsTagRegistedByOthers(tag)))
                    {
                        Thread.Sleep(1);
                    }
                    break;
                case GOTO_NEXT_GOAL_CONFIRM_RESULT.WAIT_TRAFFIC_CONTROL:
                    LOG.Critical($"{Agv.Name} is waiting Traffic Control Action Finish");
                    MoveTaskEvent.TrafficResponse.Wait_Traffic_Control_Finish_ResetEvent.WaitOne();
                    HandleTrafficControlAction(confirmArg, ref OriginalTaskDownloadData);
                    break;
                case clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.REPLAN:
                    TrafficWaitingState.SetStatusNoWaiting();
                    OriginalTaskDownloadData.Trajectory = confirmArg.TrafficResponse.NewTrajectory;
                    break;
                case clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.CANCEL:
                    TrafficWaitingState.SetStatusNoWaiting();
                    break;
                default:
                    break;
            }
        }
        private bool WaitAGVReachGoal(int goal_id)
        {
            LOG.INFO($"Wait {Agv.Name} Reach-{goal_id}");
            CancellationTokenSource _cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (Agv.states.Last_Visited_Node != goal_id)
            {
                Thread.Sleep(1);
                if (_cancellation.IsCancellationRequested)
                    throw new Exceptions.WaitAGVReachGoalTimeoutException($"Wait {Agv.Name} Reach tag-{goal_id} timeout");
            }
            return true;
        }

        public virtual List<List<MapPoint>> GenSequenceTaskByTrafficCheckPoints(List<MapPoint> stations)
        {

            List<List<MapPoint>> sequenceList = new List<List<MapPoint>>();

            List<MapPoint> traffic_eheck_points = stations.Where(station => stations.IndexOf(station) > 0 && station.IsTrafficCheckPoint).ToList();

            if (traffic_eheck_points.Count == 0)
            {
                sequenceList.Add(stations);
                return sequenceList;
            }

            sequenceList = traffic_eheck_points.Select(point => stations.Take(stations.IndexOf(point) + 1).ToList()).ToList();
            if (!sequenceList.Last().SequenceEqual(stations))
                sequenceList.Add(stations);
            return sequenceList;
        }
    }
}

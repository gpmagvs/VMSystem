using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using static AGVSystemCommonNet6.MAP.PathFinder;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.DATABASE.Helpers;
using System.Diagnostics;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Alarm;

namespace VMSystem.AGV.TaskDispatch
{
    /// <summary>
    /// 追蹤AGV任務鍊
    /// </summary>
    public class clsAGVTaskTrack
    {
        public IAGV AGV;
        protected TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();

        public clsTaskDto TaskOrder;
        public string TaskName => TaskOrder == null ? "" : TaskOrder.TaskName;
        public ACTION_TYPE TaskAction => TaskOrder == null ? ACTION_TYPE.None : TaskOrder.Action;
        public Queue<clsSegmentTask> SegmentTasksQueue { get; set; } = new Queue<clsSegmentTask>();
        public MapPoint SourcePoint { get; set; }
        public MapPoint DestinePoint { get; set; }
        public MapPoint AGVFinalLocPoint { get; set; }
        public MapPoint AGVFinalLocPoint_Carry_Start { get; set; }
        public MapPoint AGVFinalLocPoint_Carry_End { get; set; }
        private PathFinder pathFinder = new PathFinder();
        public clsWaitingInfo waitingInfo { get; set; } = new clsWaitingInfo();
        public clsPathInfo TrafficInfo { get; set; } = new clsPathInfo();

        private int taskSequence = 0;
        public List<int> RemainTags
        {
            get
            {
                try
                {

                    if (TrafficInfo.stations.Count == 0)
                        return new List<int>();

                    var currentindex = TrafficInfo.stations.IndexOf(AGVCurrentPoint);
                    if (currentindex < 0)
                        return new List<int>();
                    var remian_traj = new MapPoint[TrafficInfo.stations.Count - currentindex];
                    TrafficInfo.stations.CopyTo(currentindex, remian_traj, 0, remian_traj.Length);
                    return remian_traj.Select(r => r.TagNumber).ToList();
                }
                catch (Exception ex)
                {
                    return new List<int>();
                }
            }
        }
        private MapPoint AGVCurrentPoint => AGV.currentMapPoint;
        private STATION_TYPE AGVCurrentPointStationType => AGVCurrentPoint.StationType;
        clsTaskDownloadData TrackingTask;
        private ACTION_TYPE previousCompleteAction = ACTION_TYPE.Unknown;
        private ACTION_TYPE carryTaskCompleteAction = ACTION_TYPE.Unknown;
        public ACTION_TYPE currentActionType { get; private set; } = ACTION_TYPE.Unknown;

        private TASK_RUN_STATUS _TaskRunningStatus = TASK_RUN_STATUS.WAIT;
        public TASK_RUN_STATUS TaskRunningStatus
        {
            get => _TaskRunningStatus;
            set
            {
                if (_TaskRunningStatus != value)
                {
                    _TaskRunningStatus = value;
                    if (_TaskRunningStatus == TASK_RUN_STATUS.CANCEL)
                    {
                        PostTaskCancelRequestToAGVAsync(new clsCancelTaskCmd
                        {
                            ResetMode = RESET_MODE.CYCLE_STOP,
                            Task_Name = TaskName,
                            TimeStamp = DateTime.Now
                        });
                    }
                }
            }
        }


        private string HttpHost => $"http://{AGV.options.HostIP}:{AGV.options.HostPort}";
        public clsAGVTaskTrack()
        {

            StartTaskStatusWatchDog();
        }

        private void StartTaskStatusWatchDog()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    Thread.Sleep(100);

                    TaskRunningStatus = TaskOrder == null ? TASK_RUN_STATUS.NO_MISSION : TaskDBHelper.GetTaskStateByID(TaskName);
                }
            });
        }

        public void Start(IAGV AGV, clsTaskDto TaskOrder)
        {
            taskSequence = 0;
            SegmentTasksQueue.Clear();
            waitingInfo.IsWaiting = false;
            previousCompleteAction = ACTION_TYPE.Unknown;
            this.AGV = AGV;
            this.TaskOrder = TaskOrder;
            GetSourceAndDestineMapPoint();
            DetermineAGVFinalDestinePoint();
            SendTaskToAGV();
        }

        private async void SendTaskToAGV()
        {

            TrackingTask = GetTaskDownloadData();
            if (TrackingTask == null)
            {
                TrafficInfo = new clsPathInfo();
                ChangeTaskStatus(TASK_RUN_STATUS.ACTION_FINISH);
                return;
            }
            TrafficInfo = TrackingTask.TrafficInfo;
            await HandleRegistPoint(TrackingTask.TrafficInfo);

            AddSegmentTask(TrackingTask);
            var _returnDto = await PostTaskRequestToAGVAsync(TrackingTask);
            taskSequence += 1;
        }


        public void HandleAGVFeedback(FeedbackData feedbackData)
        {
            var task_simplex = feedbackData.TaskSimplex;
            var task_status = feedbackData.TaskStatus;
            switch (task_status)
            {
                case TASK_RUN_STATUS.NO_MISSION:
                    break;
                case TASK_RUN_STATUS.NAVIGATING:
                    break;
                case TASK_RUN_STATUS.REACH_POINT_OF_TRAJECTORY:
                    break;
                case TASK_RUN_STATUS.ACTION_START:
                    break;
                case TASK_RUN_STATUS.ACTION_FINISH:
                    var currentSegmentTask = SegmentTasksQueue.Dequeue();
                    var _CompleteAction = currentSegmentTask.taskDownloadToAGV.Action_Type;
                    if (currentSegmentTask.DestinePoint.TagNumber == AGVCurrentPoint.TagNumber)
                    {
                        this.previousCompleteAction = (_CompleteAction == ACTION_TYPE.Discharge | _CompleteAction == ACTION_TYPE.Unpark) ? ACTION_TYPE.Unknown : _CompleteAction;
                        carryTaskCompleteAction = carryTaskCompleteAction == ACTION_TYPE.Unload ? ACTION_TYPE.Unload : _CompleteAction;
                        if (!waitingInfo.IsWaiting)
                        {
                            SendTaskToAGV();
                        }
                    }
                    break;
                case TASK_RUN_STATUS.WAIT:
                    break;
                case TASK_RUN_STATUS.FAILURE:
                    break;
                case TASK_RUN_STATUS.CANCEL:
                    break;
                default:
                    break;
            }


        }

        private void DetermineAGVFinalDestinePoint()
        {
            var actionType = TaskOrder.Action;
            var DestinePoint = StaMap.GetPointByTagNumber(int.Parse(TaskOrder.To_Station));
            var SourcePoint = StaMap.GetPointByTagNumber(int.Parse(TaskOrder.From_Station));
            if (actionType == ACTION_TYPE.None | actionType == ACTION_TYPE.Charge | actionType == ACTION_TYPE.Park | actionType == ACTION_TYPE.Discharge | actionType == ACTION_TYPE.Unpark)
                AGVFinalLocPoint = DestinePoint;
            else
            {
                if (actionType == ACTION_TYPE.Carry)
                {
                    AGVFinalLocPoint_Carry_Start = StaMap.GetPointByIndex(SourcePoint.Target.Keys.First());
                    AGVFinalLocPoint_Carry_End = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First());
                }
                AGVFinalLocPoint = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First());
            }
        }

        private async Task HandleRegistPoint(clsPathInfo trafficInfo)
        {
            var Trajectory = trafficInfo.stations.Select(pt => pt.TagNumber).ToList();
            while (trafficInfo.waitPoints.Count != 0)
            {
                await Task.Delay(1);
                trafficInfo.waitPoints.TryDequeue(out MapPoint waitPoint);
                var index_of_waitPoint = Trajectory.IndexOf(Trajectory.First(pt => pt == waitPoint.TagNumber));
                var index_of_conflicPoint = index_of_waitPoint + 1;
                var conflicPoint = Trajectory[index_of_conflicPoint];
                var conflicMapPoint = StaMap.GetPointByTagNumber(conflicPoint);

                clsMapPoint[] newTrajectory = new clsMapPoint[index_of_waitPoint + 1];
                clsTaskDownloadData subTaskRunning = JsonConvert.DeserializeObject<clsTaskDownloadData>(TrackingTask.ToJson());
                try
                {

                    subTaskRunning.Trajectory.ToList().CopyTo(0, newTrajectory, 0, newTrajectory.Length);
                    subTaskRunning.Trajectory = newTrajectory;
                }
                catch (Exception)
                {
                    continue;
                }
                AddSegmentTask(subTaskRunning);
                LOG.INFO($"{AGV.Name} 在 {waitPoint.TagNumber} 等待 {conflicMapPoint.TagNumber} 淨空");
                if (newTrajectory.Length > 0)
                {
                    Console.WriteLine($"[{subTaskRunning.Task_Name}-{subTaskRunning.Task_Sequence}] Segment Traj:, " + string.Join("->", subTaskRunning.ExecutingTrajecory.Select(p => p.Point_ID)));
                    foreach (var pt in subTaskRunning.TrafficInfo.stations)
                    {
                        StaMap.RegistPoint(AGV.Name, pt);
                    }
                    var _returnDto = await PostTaskRequestToAGVAsync(subTaskRunning);
                    if (_returnDto.ReturnCode != RETURN_CODE.OK)
                    {
                        ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
                        break;
                    }
                    ChangeTaskStatus(TASK_RUN_STATUS.NAVIGATING);
                }
                bool IsWaitingPoint()
                {
                    return conflicMapPoint.IsRegisted | VMSManager.AllAGV.Any(agv => agv.currentMapPoint.TagNumber == conflicMapPoint.TagNumber);
                }
                bool Iswait = IsWaitingPoint();
                CancellationTokenSource waitAGVReachWaitPtCts = new CancellationTokenSource();

                if (Iswait)
                {
                    _ = Task.Run(() =>
                    {
                        while (AGVCurrentPoint.TagNumber != waitPoint.TagNumber)
                        {
                            if (waitAGVReachWaitPtCts.IsCancellationRequested)
                                return;
                        }
                        waitingInfo.IsWaiting = true;
                        waitingInfo.WaitingPoint = conflicMapPoint;
                    });
                }
                while (Iswait = IsWaitingPoint())
                {
                    Thread.Sleep(1);
                    if (TaskRunningStatus == TASK_RUN_STATUS.CANCEL | TaskRunningStatus == TASK_RUN_STATUS.FAILURE)
                    {
                        return;
                    }
                    TASK_RUN_STATUS runStatus = TaskDBHelper.GetTaskStateByID(TaskName);
                    if (runStatus != TASK_RUN_STATUS.NAVIGATING)
                    {
                        waitingInfo.IsWaiting = false;
                        ChangeTaskStatus(runStatus);
                        return;
                    }
                }
                waitAGVReachWaitPtCts.Cancel();
                waitingInfo.IsWaiting = false;
                Console.WriteLine($"Tag {waitPoint.TagNumber} 已Release，當前位置={AGVCurrentPoint.TagNumber}");
                //剩餘路徑
                var currentindex = trafficInfo.stations.IndexOf(AGVCurrentPoint);
                var remian_traj = new MapPoint[trafficInfo.stations.Count - currentindex];
                trafficInfo.stations.CopyTo(currentindex, remian_traj, 0, remian_traj.Length);
                var remain_tags = remian_traj.Select(pt => pt.TagNumber);
                var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv.Name != this.AGV.Name);
                int index = 0;

                foreach (var pt in remian_traj)
                {
                    var ocupy_agv = otherAGVList.FirstOrDefault(_agv => _agv.states.Last_Visited_Node == pt.TagNumber);
                    if (ocupy_agv != null)
                    {
                        var tag = ocupy_agv.currentMapPoint.TagNumber;
                        try
                        {
                            var newWaitPt = remian_traj[index - 1];
                            LOG.WARN($"新增等待點:{newWaitPt.TagNumber}");
                            trafficInfo.waitPoints.Enqueue(newWaitPt);
                        }
                        catch (Exception)
                        {
                            continue;
                        }


                    }
                    index++;
                }

            }
        }

        public async Task<SimpleRequestResponse> PostTaskCancelRequestToAGVAsync(clsCancelTaskCmd data)
        {
            try
            {
                SimpleRequestResponse taskStateResponse = await Http.PostAsync<clsCancelTaskCmd, SimpleRequestResponse>($"{HttpHost}/api/TaskDispatch/Cancel", data);
                return taskStateResponse;
            }
            catch (Exception ex)
            {
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }

        public async Task<SimpleRequestResponse> PostTaskRequestToAGVAsync(clsTaskDownloadData data)
        {
            try
            {

                SimpleRequestResponse taskStateResponse = await Http.PostAsync<clsTaskDownloadData, SimpleRequestResponse>($"{HttpHost}/api/TaskDispatch/Execute", data);
                return taskStateResponse;
            }
            catch (Exception ex)
            {
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }
        private void AddSegmentTask(clsTaskDownloadData subTaskRunning)
        {
            SegmentTasksQueue.Enqueue(new clsSegmentTask(this.TaskName, taskSequence, subTaskRunning));

        }

        public void AddSegmentTask(int sequence)
        {
        }

        public clsTaskDownloadData GetTaskDownloadData()
        {
            if (TaskOrder == null)
                return null;

            if (TaskOrder.Action == ACTION_TYPE.Carry)
            {
                if (carryTaskCompleteAction == ACTION_TYPE.Load)
                    return null;
            }
            else
            {
                if (TaskOrder.Action != ACTION_TYPE.None) //charge dis
                {
                    if (previousCompleteAction == TaskOrder.Action)
                        return null;
                }
                else
                {
                    if (AGVFinalLocPoint.TagNumber == AGV.currentMapPoint.TagNumber && Math.Abs(AGV.states.Coordination.Theta - DestinePoint.Direction) < 5)
                        return null;
                }
            }

            //非一般點位,如果是充電站>下Discharge,其他 下 UnPark
            if (AGVCurrentPointStationType != STATION_TYPE.Normal)
            {

                return CreateExitWorkStationActionTaskJob(TaskName, AGVCurrentPoint, taskSequence, AGVCurrentPoint.IsCharge ? ACTION_TYPE.Discharge : ACTION_TYPE.Unpark);
            }
            else
            {
                try
                {
                    if (previousCompleteAction == ACTION_TYPE.Unknown)
                    {
                        var toTag = DestinePoint.TagNumber;
                        if (TaskOrder.Action != ACTION_TYPE.None)
                        {
                            var WorkingStationPoint_Destine = StaMap.GetPointByTagNumber(DestinePoint.TagNumber);
                            var WorkingStationPoint_Source = StaMap.GetPointByTagNumber(SourcePoint.TagNumber);
                            if (TaskOrder.Action == ACTION_TYPE.Carry)
                            {
                                if (previousCompleteAction != ACTION_TYPE.Unload)
                                {
                                    toTag = StaMap.GetPointByIndex(WorkingStationPoint_Source.Target.Keys.First()).TagNumber;
                                }
                                else
                                    toTag = StaMap.GetPointByIndex(WorkingStationPoint_Destine.Target.Keys.First()).TagNumber;
                            }
                            else
                            {
                                toTag = StaMap.GetPointByIndex(WorkingStationPoint_Destine.Target.Keys.First()).TagNumber;
                            }
                        }

                        return CreateMoveActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, toTag, taskSequence);
                    }
                    else
                    {
                        if (previousCompleteAction == ACTION_TYPE.None)
                        {
                            if (TaskOrder.Action == ACTION_TYPE.Charge)
                            {
                                return CreateChargeActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, DestinePoint.TagNumber, taskSequence, DestinePoint.StationType);
                            }
                            else if (TaskOrder.Action == ACTION_TYPE.Park)
                            {
                                return CreateParkingActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, DestinePoint.TagNumber, taskSequence, DestinePoint.StationType);
                            }
                            else if (TaskOrder.Action == ACTION_TYPE.Load | TaskOrder.Action == ACTION_TYPE.Unload)
                            {
                                return CreateLDULDTaskJob(TaskName, TaskOrder.Action, DestinePoint, int.Parse(TaskOrder.To_Slot), TaskOrder.Carrier_ID, taskSequence);
                            }
                            else if (TaskOrder.Action == ACTION_TYPE.Carry)
                            {
                                var secondaryPtTag = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First()).TagNumber;

                                var carry_action_ = AGVCurrentPoint.TagNumber == secondaryPtTag ? ACTION_TYPE.Load : ACTION_TYPE.Unload;
                                return CreateLDULDTaskJob(TaskName, carry_action_, carry_action_ == ACTION_TYPE.Unload ? SourcePoint : DestinePoint, int.Parse(TaskOrder.To_Slot), TaskOrder.Carrier_ID, taskSequence);

                            }
                        }
                        else
                        {
                            if (previousCompleteAction == ACTION_TYPE.Unload && TaskOrder.Action == ACTION_TYPE.Carry)
                            {
                                var toTag = StaMap.GetPointByIndex(DestinePoint.Target.Keys.First()).TagNumber;
                                return CreateMoveActionTaskJob(TaskName, AGVCurrentPoint.TagNumber, toTag, taskSequence);

                            }
                        }
                    }
                    return null;
                }
                catch (NoPathForNavigatorException ex)
                {
                    AlarmManagerCenter.AddAlarm(ex.Alarm_Code);
                    return null;
                }

            }
        }
        private void GetSourceAndDestineMapPoint()
        {
            string sourceTagStr = TaskOrder.From_Station;
            string destineTagStr = TaskOrder.To_Station;

            SourcePoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == sourceTagStr);
            DestinePoint = StaMap.Map.Points.Values.FirstOrDefault(pt => pt.TagNumber.ToString() == destineTagStr);

        }
        private clsTaskDownloadData CreateMoveActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            currentActionType = ACTION_TYPE.None;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.None,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }
        private clsTaskDownloadData CreateChargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {

            currentActionType = ACTION_TYPE.Charge;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Charge,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            actionData.TrafficInfo = pathPlanDto;
            return actionData;
        }
        private clsTaskDownloadData CreateParkingActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {

            currentActionType = ACTION_TYPE.Park;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Park,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            actionData.TrafficInfo = pathPlanDto;
            return actionData;
        }

        private clsTaskDownloadData CreateExitWorkStationActionTaskJob(string taskName, MapPoint currentMapPoint, int task_seq, ACTION_TYPE aCTION_TYPE = ACTION_TYPE.Discharge)
        {
            currentActionType = aCTION_TYPE;
            MapPoint next_point = StaMap.Map.Points[currentMapPoint.Target.First().Key];
            int fromTag = currentMapPoint.TagNumber;
            int toTag = next_point.TagNumber;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = aCTION_TYPE,
                Destination = toTag,
                Task_Name = taskName,
                Station_Type = 0,
                Task_Sequence = task_seq,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }

        private clsTaskDownloadData CreateLDULDTaskJob(string TaskName, ACTION_TYPE Action, MapPoint EQPoint, int to_slot, string cstID, int TaskSeq)
        {
            currentActionType = Action;
            var fromTag = StaMap.Map.Points[EQPoint.Target.First().Key].TagNumber;
            var pathPlanDto = OptimizdPathFind(fromTag, EQPoint.TagNumber);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = Action,
                Destination = EQPoint.TagNumber,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = EQPoint.StationType,
                Task_Sequence = TaskSeq,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            actionData.TrafficInfo = pathPlanDto;
            return actionData;
        }

        private clsPathInfo OptimizdPathFind(int fromTag, int toTag)
        {
            var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != this.AGV);
            clsPathInfo pathInfo = new clsPathInfo();
            StaMap.TryGetPointByTagNumber(toTag, out MapPoint FinalPoint);
            var regitedPoints = TrafficControlCenter.DynamicTrafficState.RegistedPoints;
            var toAvoidPointsTags = regitedPoints.FindAll(pt => pt.RegistInfo?.RegisterAGVName != AGV.Name && pt.TagNumber != AGVCurrentPoint.TagNumber).Select(pt => pt.TagNumber).ToList();
            toAvoidPointsTags.AddRange(otherAGVList.Select(agv => agv.states.Last_Visited_Node));//考慮移動路徑
            //toAvoidPointsTags = toAvoidPointsTags.FindAll(pt => !otherAGVList.Select(agv => agv.currentMapPoint.TagNumber).Contains(pt));


            var option = new PathFinderOption
            {
                ConstrainTags = toAvoidPointsTags
            };

            option.ConstrainTags = option.ConstrainTags.Distinct().ToList();

            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag, option);//考慮AGV阻擋下，最短路徑

            if (pathPlanDto == null) //沒有任何路線可以行走
            {
                var shortestPathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);//不考慮AGV阻擋下，最短路徑
                if (shortestPathPlanDto == null)
                {

                    throw new NoPathForNavigatorException();
                }

                var pathTags = shortestPathPlanDto.stations.Select(pt => pt.TagNumber).ToList();
                Dictionary<int, MapPoint> waitPointsDict = new Dictionary<int, MapPoint>();
                foreach (var p in option.ConstrainTags)
                {
                    var index = pathTags.IndexOf(p) - 1;
                    if (index >= 0)
                        waitPointsDict.Add(index, shortestPathPlanDto.stations[index]);
                }
                var waitPoints = waitPointsDict.OrderBy(kp => kp.Key).Select(kp => kp.Value);


                foreach (var pt in waitPoints)
                {
                    pathInfo.waitPoints.Enqueue(pt);
                }
                pathInfo.stations = shortestPathPlanDto.stations;
                return pathInfo;
            }
            else
            {
                foreach (var pt in pathPlanDto.stations)
                {
                    StaMap.RegistPoint(AGV.Name, pt);
                }
                return pathPlanDto;
            }
        }
        private void ChangeTaskStatus(TASK_RUN_STATUS status, string failure_reason = "")
        {
            if (TaskOrder == null)
                return;
            TaskOrder.State = status;
            if (status == TASK_RUN_STATUS.FAILURE | status == TASK_RUN_STATUS.CANCEL | status == TASK_RUN_STATUS.ACTION_FINISH)
            {
                waitingInfo.IsWaiting = false;
                TaskOrder.FailureReason = failure_reason;
                TaskOrder.FinishTime = DateTime.Now;
                TaskDBHelper.Update(TaskOrder);
                TaskOrder = null;
            }
            else
            {
                TaskDBHelper.Update(TaskOrder);
            }
        }
    }

    public class clsSegmentTask
    {
        public string Task_Simple { get; set; }
        public clsTaskDownloadData taskDownloadToAGV { get; set; }
        public TASK_RUN_STATUS Run_Status { get; set; } = TASK_RUN_STATUS.WAIT;
        public MapPoint DestinePoint { get; set; }
        public clsSegmentTask(string taskName, int sequence, clsTaskDownloadData taskDownloadToAGV)
        {
            Task_Simple = taskName + $"-{sequence}";
            Sequence = sequence;
            taskDownloadToAGV.Task_Sequence = sequence;
            this.taskDownloadToAGV = taskDownloadToAGV;
            DetermineDestinePoint();
        }

        private void DetermineDestinePoint()
        {
            var actionType = taskDownloadToAGV.Action_Type;
            if (actionType == ACTION_TYPE.None | actionType == ACTION_TYPE.Charge | actionType == ACTION_TYPE.Park | actionType == ACTION_TYPE.Discharge | actionType == ACTION_TYPE.Unpark)
                DestinePoint = StaMap.GetPointByTagNumber(taskDownloadToAGV.Destination);
            else
            {
                var WorkingStationPoint = StaMap.GetPointByTagNumber(taskDownloadToAGV.Destination);
                DestinePoint = StaMap.GetPointByIndex(WorkingStationPoint.Target.Keys.First());
            }
        }

        public int Sequence { get; }
    }

}

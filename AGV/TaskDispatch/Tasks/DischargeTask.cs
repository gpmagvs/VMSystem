using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.TrafficControl;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class DischargeTask : TaskBase
    {
        public DischargeTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override void CreateTaskToAGV()
        {
            MapPoint destinMapPoint = StaMap.GetPointByIndex(AGVCurrentMapPoint.Target.Keys.First());
            base.CreateTaskToAGV();
            this.TaskDonwloadToAGV.Destination = destinMapPoint.TagNumber;
            this.TaskDonwloadToAGV.Homing_Trajectory = new clsMapPoint[2]
            {
                MapPointToTaskPoint(AGVCurrentMapPoint,index:0),
                MapPointToTaskPoint(destinMapPoint,index:1)
            };

        }
        public override async Task SendTaskToAGV()
        {
            DetermineThetaOfDestine(this.TaskDonwloadToAGV);
            clsLeaveFromWorkStationConfirmEventArg TrafficResponse = await HandleAgvLeaveFromWorkstationTaskSend(new clsLeaveFromWorkStationConfirmEventArg
            {
                Agv = this.Agv,
                GoalTag = TaskDonwloadToAGV.Destination
            });
            if (TrafficResponse.ActionConfirm == clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK)
                await base.SendTaskToAGV();

        }
        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
        }

        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            _taskDownloadData.Homing_Trajectory.Last().Theta = _taskDownloadData.Homing_Trajectory.First().Theta;
        }

        public override VehicleMovementStage Stage { get; } = VehicleMovementStage.LeaveFrom_ChargeStation;
        public override ACTION_TYPE ActionType => ACTION_TYPE.Discharge;


        private async Task<clsLeaveFromWorkStationConfirmEventArg> HandleAgvLeaveFromWorkstationTaskSend(clsLeaveFromWorkStationConfirmEventArg args)
        {
            var otherAGVList = VMSManager.AllAGV.FilterOutAGVFromCollection(args.Agv);
            try
            {
                if (IsNeedWait(args.GoalTag, args.Agv, otherAGVList, out bool isTagRegisted, out bool isTagBlocked, out bool isInterference, out bool isInterfercenWhenRotation))
                {
                    args.WaitSignal.Reset();
                    args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.WAIT;

                    clsWaitingInfo TrafficWaittingInfo = args.Agv.taskDispatchModule.OrderHandler.RunningTask.TrafficWaitingState;
                    while (IsNeedWait(args.GoalTag, args.Agv, otherAGVList, out isTagRegisted, out isTagBlocked, out isInterference, out isInterfercenWhenRotation))
                    {
                        if (IsTaskCanceled)
                        {
                            throw new TaskCanceledException();
                        }
                        string _waitingMessage = CreateWaitingInfoDisplayMessage(args, isTagRegisted, isTagBlocked, isInterfercenWhenRotation);
                        TrafficWaittingInfo.SetStatusWaitingConflictPointRelease(new List<int> { args.GoalTag }, _waitingMessage);
                        await Task.Delay(100);
                    }
                    TrafficWaittingInfo.SetStatusNoWaiting();
                    args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;


                }
                else
                    args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.OK;
                return args;
            }
            catch (TaskCanceledException)
            {
                args.ActionConfirm = clsLeaveFromWorkStationConfirmEventArg.LEAVE_WORKSTATION_ACTION.CANCEL;
                return args;
            }


            #region region method
            string CreateWaitingInfoDisplayMessage(clsLeaveFromWorkStationConfirmEventArg args, bool isTagRegisted, bool isTagBlocked, bool isInterfercenWhenRotation)
            {
                // 定义基础消息
                string baseMessage = "等待通行";

                // 根据条件选择具体的提示信息
                if (isInterfercenWhenRotation)
                {
                    return $"{baseMessage}-抵達終點後旋轉可能會與其他車輛干涉";
                }
                else if (isTagRegisted)
                {
                    return $"{baseMessage}-{args.GoalTag} 被其他車輛註冊";
                }
                else if (isTagBlocked)
                {
                    return $"{baseMessage}-{args.GoalTag}有AGV無法通行";
                }
                else
                {
                    return $"{baseMessage}-與其他車輛干涉";
                }
            }
            bool IsPathInterference(int goal, IEnumerable<IAGV> _otherAGVList)
            {
                MapPoint[] path = new MapPoint[2] { args.Agv.currentMapPoint, StaMap.GetPointByTagNumber(goal) };
                return TrafficControl.Tools.CalculatePathInterference(path, args.Agv, _otherAGVList, true);
            }

            bool IsDestineBlocked()
            {
                return otherAGVList.Any(agv => agv.states.Last_Visited_Node == args.GoalTag);
            }
            bool IsDestineRegisted(int goal, string AgvName)
            {
                if (!StaMap.RegistDictionary.TryGetValue(goal, out var result))
                    return false;

                return result.RegisterAGVName != AgvName;
            }

            bool IsNeedWait(int _goalTag, IAGV agv, IEnumerable<IAGV> _otherAGVList, out bool isTagRegisted, out bool isTagBlocked, out bool isInterference, out bool isInterfercenWhenRotation)
            {

                var goalPoint = StaMap.GetPointByTagNumber(_goalTag);
                MapCircleArea _agvCircleAreaWhenReachGoal = agv.AGVRotaionGeometry.Clone();
                _agvCircleAreaWhenReachGoal.SetCenter(goalPoint.X, goalPoint.Y);
                isTagRegisted = IsDestineRegisted(_goalTag, agv.Name);
                isInterference = IsPathInterference(_goalTag, _otherAGVList);
                isInterfercenWhenRotation = _otherAGVList.Any(agv => agv.AGVRotaionGeometry.IsIntersectionTo(_agvCircleAreaWhenReachGoal));
                isTagBlocked = IsDestineBlocked();
                return isTagRegisted || isInterference || isTagBlocked || isInterfercenWhenRotation;
            }
            #endregion
        }

        public override void CancelTask()
        {
            base.CancelTask();
        }
    }
}

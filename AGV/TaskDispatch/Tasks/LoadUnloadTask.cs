using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using VMSystem.TrafficControl;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public abstract class LoadUnloadTask : TaskBase
    {
        private MapPoint EntryPoint = new();
        private MapPoint EQPoint = new();

        public LoadUnloadTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage => throw new NotImplementedException();

        public override ACTION_TYPE ActionType => throw new NotImplementedException();

        public override bool IsAGVReachDestine
        {
            get
            {
                return Agv.states.Last_Visited_Node == this.TaskDonwloadToAGV.Homing_Trajectory[0].Point_ID;
            }
        }
        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            throw new NotImplementedException();
        }
        protected abstract void UpdateActionDisplay();
        public override void CreateTaskToAGV()
        {

            base.CreateTaskToAGV();

            EQPoint = StaMap.GetPointByTagNumber(GetDestineWorkStationTagByOrderInfo(OrderData));
            EntryPoint = GetEntryPointsOfWorkStation(EQPoint, Agv.currentMapPoint);

            this.TaskDonwloadToAGV.Height = GetSlotHeight();
            this.TaskDonwloadToAGV.Destination = EQPoint.TagNumber;
            this.TaskDonwloadToAGV.Homing_Trajectory = new clsMapPoint[2]
            {
                MapPointToTaskPoint(EntryPoint,index:0),
                MapPointToTaskPoint(EQPoint,index:1)
            };
            MoveTaskEvent = new clsMoveTaskEvent(Agv, new List<int> { EntryPoint.TagNumber, EQPoint.TagNumber }, null, false);
        }
        public override async Task SendTaskToAGV()
        {
            Agv.NavigationState.LeaveWorkStationHighPriority = Agv.NavigationState.IsWaitingForLeaveWorkStation = false;
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            if (TrafficControlCenter.TrafficControlParameters.Basic.UnLockEntryPointWhenParkAtEquipment)
            {
                int currentTag = Agv.currentMapPoint.TagNumber;
                await StaMap.UnRegistPoint(Agv.Name, currentTag);
                NotifyServiceHelper.WARNING($"[!] {Agv.Name} 進入設備解除 {currentTag} 註冊");
            }
            UpdateEQActionMessageDisplay();
            ChangeWorkStationMoveStateBackwarding();
            await base.SendTaskToAGV();
        }
        private async Task ChangeWorkStationMoveStateBackwarding()
        {
            await Task.Delay(1500);
            Agv.NavigationState.WorkStationMoveState = VehicleNavigationState.WORKSTATION_MOVE_STATE.FORWARDING;
            await Task.Delay(1500);
            Agv.NavigationState.WorkStationMoveState = VehicleNavigationState.WORKSTATION_MOVE_STATE.BACKWARDING;
        }
        internal async Task UpdateEQActionMessageDisplay()
        {
            ACTION_TYPE orderAction = OrderData.Action;
            string actionString = "";
            string sourceDestineString = "";
            if (orderAction == ACTION_TYPE.Carry)
            {
                MapPoint fromPt = StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
                MapPoint toPt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                sourceDestineString = ActionType == ACTION_TYPE.Load ? $"(來源 {fromPt.Graph.Display})" : $"(終點 {toPt.Graph.Display})";
            }
            actionString = this.ActionType == ACTION_TYPE.Load ? "放貨" : "取貨";
            await Task.Delay(1000);
            UpdateMoveStateMessage($"{EQPoint.Graph.Display} [{actionString}] 中...\r\n{sourceDestineString}");

        }
        public override void UpdateMoveStateMessage(string msg)
        {
            TrafficWaitingState.SetDisplayMessage($"{msg}");
        }
        protected abstract int GetSlotHeight();
        internal override void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            base.HandleAGVNavigatingFeedback(feedbackData);

            if (feedbackData.LastVisitedNode == DestineTag)
            {
                MoveTaskEvent.AGVRequestState.OptimizedToDestineTrajectoryTagList.Reverse();
            }
        }
        protected virtual int GetDestineWorkStationTagByOrderInfo(clsTaskDto orderInfo)
        {
            if (orderInfo.Action == ACTION_TYPE.Load || orderInfo.Action == ACTION_TYPE.Unload)
            {
                return orderInfo.To_Station_Tag;
            }
            else
            {
                if (this.ActionType == ACTION_TYPE.Unload)
                    return orderInfo.From_Station_Tag;
                else
                    return orderInfo.To_Station_Tag;
            }
        }
    }
}

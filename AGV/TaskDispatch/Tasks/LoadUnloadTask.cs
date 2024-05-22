﻿using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public abstract class LoadUnloadTask : TaskBase
    {
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

            MapPoint destinMapPoint = StaMap.GetPointByTagNumber(GetDestineWorkStationTagByOrderInfo(OrderData));
            MapPoint sourceMapPoint = GetEntryPointsOfWorkStation(destinMapPoint, Agv.currentMapPoint);

            this.TaskDonwloadToAGV.Height = GetSlotHeight();
            this.TaskDonwloadToAGV.Destination = destinMapPoint.TagNumber;
            this.TaskDonwloadToAGV.Homing_Trajectory = new clsMapPoint[2]
            {
                MapPointToTaskPoint(sourceMapPoint,index:0),
                MapPointToTaskPoint(destinMapPoint,index:1)
            };
            MoveTaskEvent = new clsMoveTaskEvent(Agv, new List<int> { sourceMapPoint.TagNumber, destinMapPoint.TagNumber }, null, false);
        }
        public override async Task SendTaskToAGV()
        {
            Agv.NavigationState.LeaveWorkStationHighPriority = Agv.NavigationState.IsWaitingForLeaveWorkStation = false;
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            if (AGVSConfigulator.SysConfigs.TaskControlConfigs.UnLockEntryPointWhenParkAtEquipment)
            {
                int currentTag = Agv.currentMapPoint.TagNumber;
                await StaMap.UnRegistPoint(Agv.Name, currentTag);
                NotifyServiceHelper.WARNING($"[!] {Agv.Name} 進入設備解除 {currentTag} 註冊");
            }
            await base.SendTaskToAGV();
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

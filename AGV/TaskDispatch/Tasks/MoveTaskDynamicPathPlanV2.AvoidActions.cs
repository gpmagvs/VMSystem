using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.TrafficControl;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public partial class MoveTaskDynamicPathPlanV2
    {
        private async Task AvoidActionProcess()
        {
            if (subStage == VehicleMovementStage.AvoidPath_Park)
            {

                MapPoint secondaryPt = Agv.NavigationState.AvoidActionState.AvoidToPtMoveDestine;
                MapPoint parkPortPt = Agv.NavigationState.AvoidActionState.AvoidPt;
                clsMapPoint[] homingTrajectory = new MapPoint[2] { secondaryPt, parkPortPt }.Select(pt => MapPointToTaskPoint(pt)).ToArray();


                bool IsReachSecondaryPt(out string msg)
                {
                    msg = string.Empty;
                    bool tagMatch = this.Agv.currentMapPoint.TagNumber == secondaryPt.TagNumber;
                    msg += tagMatch ? "" : "未抵達,";
                    bool statusCorrect = this.Agv.main_state == clsEnums.MAIN_STATUS.IDLE;
                    msg += statusCorrect ? "" : "狀態非閒置,";
                    bool forwardThetaCorrect = false;
                    //Agv.states.Coordination.Theta;
                    double thetaExpect = Tools.CalculationForwardAngle(homingTrajectory[0], homingTrajectory[1]);
                    forwardThetaCorrect = Tools.CalculateTheateDiff(thetaExpect, Agv.states.Coordination.Theta) < 5;
                    msg += statusCorrect ? "" : "角度錯誤";
                    return tagMatch && statusCorrect && forwardThetaCorrect;
                }

                while (!IsReachSecondaryPt(out string msg))
                {
                    await Task.Delay(1000);
                    UpdateStateDisplayMessage($"Move to Park Station[{msg}]");
                }


                ParkTask parkTask = new ParkTask(this.Agv, this.OrderData);
                (TaskDownloadRequestResponse response, clsMapPoint[] trajectory) = await parkTask._DispatchTaskToAGV(new clsTaskDownloadData
                {
                    Action_Type = ACTION_TYPE.Park,
                    Destination = Agv.NavigationState.AvoidActionState.AvoidPt.TagNumber,
                    Height = 0,
                    Task_Name = this.OrderData.TaskName,
                    Homing_Trajectory = homingTrajectory
                });

                if (response.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                    throw new AGVRejectTaskException();
                Agv.NavigationState.UpdateNavigationPoints(new MapPoint[2] { secondaryPt, parkPortPt });
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                Agv.NavigationState.StateReset();
                UpdateStateDisplayMessage($"Wait 3 sec and leave.");
                await Task.Delay(3000);
                LeaveParkStationConflicDetection detection = new LeaveParkStationConflicDetection(secondaryPt, Agv.states.Coordination.Theta, this.Agv);
                clsConflicDetectResultWrapper detectResultWrapper = new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, "");
                while (detectResultWrapper.Result != DETECTION_RESULT.OK)
                {
                    detectResultWrapper = detection.Detect();
                    UpdateStateDisplayMessage($"{detectResultWrapper.Message}");
                    await Task.Delay(1000);
                }

                DischargeTask _leavePortTask = new DischargeTask(this.Agv, this.OrderData);
                UpdateStateDisplayMessage($"離開停車點");
                await _leavePortTask._DispatchTaskToAGV(new clsTaskDownloadData
                {
                    Action_Type = ACTION_TYPE.Discharge,
                    Destination = secondaryPt.TagNumber,
                    Task_Name = this.OrderData.TaskName,
                    Homing_Trajectory = homingTrajectory.Reverse().ToArray()
                });

                Agv.NavigationState.UpdateNavigationPoints(new MapPoint[2] { parkPortPt, secondaryPt });

                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                _previsousTrajectorySendToAGV.Clear();
            }

            subStage = Stage;
            await SendTaskToAGV(this.finalMapPoint);
        }

    }
}

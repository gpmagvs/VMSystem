﻿using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6;
using VMSystem.AGV.TaskDispatch.Exceptions;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using static AGVSystemCommonNet6.MAP.PathFinder;
using System.Diagnostics;
using AGVSystemCommonNet6.MAP.Geometry;

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

                    if (Agv.main_state == clsEnums.MAIN_STATUS.DOWN || IsTaskCanceled)
                        return;
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
            else
            {
                //計算當前位置到終點的所有路線皆沒有被另外一台衝突車輛擋道時才繼續動作
                await WaitPathToDestineNotConflicToYieldedVehicelAsync();
            }

            subStage = Stage;
            await SendTaskToAGV(this.finalMapPoint);
        }

        private async Task WaitPathToDestineNotConflicToYieldedVehicelAsync()
        {
            IAGV currentAvoidToVehicle = Agv.NavigationState.AvoidActionState.AvoidToVehicle; //正在避讓那車
            if (currentAvoidToVehicle == null)
                return;

            PathFinder _pathFinder = new PathFinder();
            clsPathInfo pathToGoalWrapper = _pathFinder.FindShortestPath(Agv.currentMapPoint.TagNumber, this.finalMapPoint.TagNumber,
                                            new PathFinder.PathFinderOption() { OnlyNormalPoint = true, Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE });

            if (pathToGoalWrapper.stations.Count == 0)
                return;

            Stopwatch _timer = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(1000);
                IAGV thisAGV = Agv;

                if (thisAGV.main_state == clsEnums.MAIN_STATUS.DOWN|| IsTaskCanceled)
                    return;

                List<MapRectangle> AGVBodyCoveringOfPath =  Tools.GetPathRegionsWithRectangle(pathToGoalWrapper.stations, thisAGV.options.VehicleWidth/100.0, thisAGV.options.VehicleLength / 100.0);
                bool isPathClear = AGVBodyCoveringOfPath.All(rect => !rect.IsIntersectionTo(currentAvoidToVehicle.AGVRealTimeGeometery));
                if (isPathClear)
                    return;
                else
                    UpdateStateDisplayMessage($"避車點(主幹道)-等待[{currentAvoidToVehicle.Name}]通行...");
                if (_timer.Elapsed.Seconds > 3 && currentAvoidToVehicle.NavigationState.IsWaitingConflicSolve && currentAvoidToVehicle.NavigationState.currentConflicToAGV.Name == thisAGV.Name)
                {
                    //又被你擋住
                    UpdateStateDisplayMessage($"避車中(主幹道)但仍與[{currentAvoidToVehicle.Name}]衝突..");
                    await Task.Delay(1000);
                    return;
                }
            }


        }
    }
}
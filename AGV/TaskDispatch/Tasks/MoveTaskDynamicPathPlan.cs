﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.GPMRosMessageNet.Messages;
using AGVSystemCommonNet6.MAP;
using Newtonsoft.Json.Linq;
using System;
using AGVSystemCommonNet6.Alarm;
using static AGVSystemCommonNet6.MAP.PathFinder;
using System.Numerics;
using System.Drawing;
using VMSystem.TrafficControl;
using System.Runtime.CompilerServices;
using System.Diagnostics.Eventing.Reader;
using AGVSystemCommonNet6.Microservices.VMS;
using VMSystem.VMS;
using System.Diagnostics;
using static AGVSystemCommonNet6.MAP.MapPoint;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.Extensions;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveTaskDynamicPathPlan : MoveTask
    {
        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling;

        protected List<clsMapPoint> _previsousTrajectorySendToAGV = new List<clsMapPoint>();
        public MoveTaskDynamicPathPlan() : base() { }
        public MoveTaskDynamicPathPlan(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData) { }
        public MoveTaskDynamicPathPlan(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override async Task SendTaskToAGV()
        {

            var token = _TaskCancelTokenSource.Token;
            await Task.Run(async () =>
            {
                try
                {
                    int _sequenceIndex = 0;
                    string task_simplex = this.TaskDonwloadToAGV.Task_Simplex;
                    _previsousTrajectorySendToAGV = new List<clsMapPoint>();
                    int pointNum = TrafficControlCenter.TrafficControlParameters.Basic.SegmentTrajectoryPointNum;
                    int pathStartTagToCal = Agv.states.Last_Visited_Node;
                    int _lastFinalEndTag = -1;
                    List<MapPoint> _lastNextPath = new List<MapPoint>();
                    bool IsNexPathHasEQReplacingParts = false;

                    bool _agvAlreadyTurnToNextPathDirection = false;
                    while (!IsAGVReachGoal(DestineTag, checkTheta: true) || _sequenceIndex == 0)
                    {
                        if (token.IsCancellationRequested || Agv.main_state == clsEnums.MAIN_STATUS.DOWN || Agv.online_state == clsEnums.ONLINE_STATE.OFFLINE || Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            token.ThrowIfCancellationRequested();

                        bool _findPath = false;
                        clsPathInfo optimzePath = null;
                        while ((optimzePath = CalculateOptimizedPath(pathStartTagToCal, true)) == null)
                        {
                            if (token.IsCancellationRequested)
                                token.ThrowIfCancellationRequested();
                            _findPath = true;
                            pathStartTagToCal = Agv.states.Last_Visited_Node;
                            TrafficWaitingState.SetStatusWaitingConflictPointRelease(new List<int>(), "Waiting...");
                            await Task.Delay(1000);
                        }

                        if (_findPath)
                        {
                            pathStartTagToCal = Agv.states.Last_Visited_Node;
                            continue;
                        }

                        List<MapPoint> nextPath = GetNextPath(optimzePath, pathStartTagToCal, out movePause, out int _tagOfBlockedByPartsReplacing, pointNum).ToList();

                        if (movePause)
                        {
                            tagOfBlockedByPartsReplacing = GetWorkStationTagByNormalPointTag(_tagOfBlockedByPartsReplacing);
                        }
                        if (_lastNextPath.Count != 0 && !nextPath.Contains(_lastNextPath.Last()))
                        {
                            logger.Fatal($"Replan . Use other path");
                            await base.SendCancelRequestToAGV();
                            while (Agv.main_state != clsEnums.MAIN_STATUS.IDLE)
                            {
                                await Task.Delay(1000);
                            }
                            pathStartTagToCal = Agv.states.Last_Visited_Node;
                            StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            _lastNextPath.Clear();
                            _previsousTrajectorySendToAGV.Clear();
                            continue;
                        }

                        _lastNextPath = nextPath;
                        //計算干涉
                        bool _waitingInterference = false;

                        bool _IsNextPathHasPointsRegisted(List<MapPoint> nextPath)
                        {
                            var registedPoints = StaMap.RegistDictionary.Where(kp => nextPath.GetTagCollection().Contains(kp.Key) && kp.Value.RegisterAGVName != Agv.Name).Select(k => k.Value);
                            return registedPoints.Any();
                        }

                        bool _isCurrentPointInFrontOfCharger = Agv.currentMapPoint.Target.Keys.Any(index => StaMap.GetPointByIndex(index).IsCharge);
                        bool _isCurrentPtisFirstPtOfWholePath = _previsousTrajectorySendToAGV.Count == 0;

                        Stopwatch timer = Stopwatch.StartNew();
                        bool _recalculateOptimizePath = false;

                        //if (!_isCurrentPointInFrontOfCharger && !_agvAlreadyTurnToNextPathDirection && _isCurrentPtisFirstPtOfWholePath)
                        //{
                        //    _agvAlreadyTurnToNextPathDirection = true;
                        //    TurnToNextPath(nextPath);
                        //}

                        while (VMSystem.TrafficControl.Tools.CalculatePathInterference(nextPath, this.Agv, out var conflicAGVList, false) || _IsNextPathHasPointsRegisted(nextPath))
                        {
                            _waitingInterference = true;

                            if (token.IsCancellationRequested)
                                token.ThrowIfCancellationRequested();
                            if (Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                TrafficWaitingState.SetStatusNoWaiting();
                                return;
                            }
                            TrafficWaitingState.SetStatusWaitingConflictPointRelease(new List<int>(), $"等待{(conflicAGVList.Any() ? $"與 {string.Join(",", conflicAGVList.Select(agv => agv.Name))} 之" : "")}路徑干涉解除");
                            if (timer.Elapsed.Seconds < 5)
                            {
                                await Task.Delay(1000);
                                continue;
                            }
                            _recalculateOptimizePath = true;
                            break;
                        }
                        if (_recalculateOptimizePath)
                        {
                            await SendCancelRequestToAGV();
                            StaMap.UnRegistPointsOfAGVRegisted(Agv);
                            MoveTaskEvent.AGVRequestState.OptimizedToDestineTrajectoryTagList.Clear();
                            _previsousTrajectorySendToAGV.Clear();
                            PassedTags.Clear();
                            pathStartTagToCal = Agv.states.Last_Visited_Node;
                            continue;
                        }
                        if (_waitingInterference)
                        {
                            pathStartTagToCal = Agv.states.Last_Visited_Node;
                            continue;
                        }

                        //await WaitNextPathPassableByEQPartsReplace(nextPath);

                        TrafficWaitingState.SetStatusNoWaiting();
                        _previsousTrajectorySendToAGV.AddRange(PathFinder.GetTrajectory(StaMap.Map.Name, nextPath));
                        _previsousTrajectorySendToAGV = _previsousTrajectorySendToAGV.DistinctBy(pt => pt.Point_ID).ToList();

                        clsTaskDownloadData _taskDownloadData = this.TaskDonwloadToAGV.Clone();
                        _taskDownloadData.Task_Sequence = _sequenceIndex;
                        _taskDownloadData.Task_Simplex = task_simplex + "-" + _sequenceIndex;
                        _taskDownloadData.Trajectory = _previsousTrajectorySendToAGV.ToArray();

                        if (Agv.model == clsEnums.AGV_TYPE.INSPECTION_AGV)
                        {
                            _taskDownloadData.Destination = _previsousTrajectorySendToAGV.Last().Point_ID;
                        }

                        int finalEndTag = _previsousTrajectorySendToAGV.Last().Point_ID;

                        if (finalEndTag == _lastFinalEndTag)
                        {
                            break;
                        }
                        _lastFinalEndTag = finalEndTag;
                        //計算停車角度
                        SettingParkAngle(ref _taskDownloadData);

                        TaskDonwloadToAGV = _taskDownloadData;
                        MoveTaskEvent = new clsMoveTaskEvent(Agv, optimzePath.tags, nextPath, OrderData.IsTrafficControlTask);


                        logger.Fatal($"Send Task To AGV when AGV last visited Tag = {Agv.states.Last_Visited_Node}");


                        (TaskDownloadRequestResponse _result, clsMapPoint[] _trajectory) = await _DispatchTaskToAGV(_taskDownloadData);
                        if (_result.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                        {
                            if (OnTaskDownloadToAGVButAGVRejected != null)
                                OnTaskDownloadToAGVButAGVRejected(_result.ReturnCode.ToAGVSAlarmCode());
                            return;
                        }

                        if (Agv.model == clsEnums.AGV_TYPE.INSPECTION_AGV)
                        {
                            _previsousTrajectorySendToAGV.Clear();
                        }


                        StaMap.RegistPoint(Agv.Name, MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList, out string ErrorMessage);
                        Agv.NavigationState.UpdateNavigationPoints(nextPath);
                        var agvLastVisitNodeIndex = nextPath.FindIndex(pt => pt.TagNumber == Agv.states.Last_Visited_Node);
                        var nextCheckPoint = pointNum == -1 || Agv.model == clsEnums.AGV_TYPE.INSPECTION_AGV ?
                                            nextPath.Last() :
                                            agvLastVisitNodeIndex == -1 || agvLastVisitNodeIndex + 1 >= nextPath.Count ? nextPath.First() : nextPath[nextPath.Count - 2];
                        //1,2,3,4,5,6)
                        await WaitAGVReachNexCheckPoint(nextCheckPoint, nextPath, token).ConfigureAwait(false);

                        TrafficWaitingState.SetStatusNoWaiting();
                        pathStartTagToCal = nextCheckPoint.TagNumber;
                        _sequenceIndex += 1;
                        await Task.Delay(10);

                    }



                    MapPoint GetGoalStationWhenNonNormalTaskExecute()
                    {
                        MapPoint _targetStation = new MapPoint();
                        if (this.Stage == VehicleMovementStage.Traveling_To_Source)
                            _targetStation = StaMap.GetPointByTagNumber(this.OrderData.From_Station_Tag);
                        else if (this.Stage == VehicleMovementStage.Traveling_To_Destine)
                            _targetStation = StaMap.GetPointByTagNumber(this.OrderData.need_change_agv && this.TransferStage == TransferStage.MoveToTransferStationLoad ?
                                this.OrderData.TransferToTag : this.OrderData.To_Station_Tag);
                        return _targetStation;
                    }

                    void SettingParkAngle(ref clsTaskDownloadData _taskDownloadData)
                    {
                        double theta = 0;
                        bool isNextStopIsFinal = _taskDownloadData.ExecutingTrajecory.Last().Point_ID == this.DestineTag;

                        if (isNextStopIsFinal && (OrderData.Action == ACTION_TYPE.None || OrderData.Action == ACTION_TYPE.ExchangeBattery))
                        {
                            logger.Warn($"Next path goal is destine, park");
                            return;
                        }

                        if (_taskDownloadData.Trajectory.Length < 2)
                        {

                            if (OrderData.Action == ACTION_TYPE.None)
                                _taskDownloadData.Trajectory.Last().Theta = _taskDownloadData.Trajectory.Last().Theta;
                            else
                            {
                                MapPoint _targetStation = GetGoalStationWhenNonNormalTaskExecute();
                                CalculateStopAngle(this.InfrontOfWorkStationPoint);
                                _taskDownloadData.Trajectory.Last().Theta = FinalStopTheta;

                            }
                            return;
                        }
                        else
                        {
                            var lastPoint = _taskDownloadData.Trajectory.Last();
                            var lastSecondPoint = _taskDownloadData.Trajectory[_taskDownloadData.Trajectory.Length - 2];

                            var lastPt = new PointF((float)lastPoint.X, (float)lastPoint.Y);
                            var lastSecondPt = new PointF((float)lastSecondPoint.X, (float)lastSecondPoint.Y);

                            if (this.Stage != VehicleMovementStage.Traveling)
                            {
                                MapPoint _targetStation = GetGoalStationWhenNonNormalTaskExecute();

                                if (InfrontOfWorkStationPoint.TagNumber == lastPoint.Point_ID)
                                {
                                    theta = FinalStopTheta;
                                    _taskDownloadData.Trajectory.Last().Theta = theta;
                                    return;
                                }
                            }

                            _taskDownloadData.Trajectory.Last().Theta = Tools.CalculationForwardAngle(lastSecondPt, lastPt); ;
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    logger.Warn($"任務-{OrderData.ActionName}(ID:{OrderData.TaskName} )已取消");

                }
                catch (Exception ex)
                {
                    throw ex;
                }
                finally
                {
                }

            }, token);
        }

        private async void TurnToNextPath(List<MapPoint> nextPath)
        {
            if (nextPath.Count < 2)
                return;
            var start = new PointF((float)nextPath[0].X, (float)nextPath[0].Y);
            var end = new PointF((float)nextPath[1].X, (float)nextPath[1].Y);
            double theta = Tools.CalculationForwardAngle(start, end);
            var _taskDto = TaskDonwloadToAGV.Clone();
            var firstPt = _taskDto.Trajectory.Take(1).First();
            _taskDto.Trajectory = new clsMapPoint[1] { firstPt.Clone() };
            _taskDto.Trajectory[0].Theta = theta;
            _taskDto.Destination = firstPt.Point_ID;


            //while (VMSystem.TrafficControl.Tools.CalculatePathInterferenceByAGVGeometry(nextPath, Agv, out var conflicAGV))
            //{
            //    TrafficWaitingState.SetDisplayMessage($"準備轉向至下一路段|等待干涉解除.");
            //    await Task.Delay(1000);
            //    if (IsTaskCanceled|| disposedValue)
            //        return;
            //}

            (TaskDownloadRequestResponse _result, clsMapPoint[] _trajectory) = _DispatchTaskToAGV(_taskDto).GetAwaiter().GetResult();
            if (_result.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
            {
                while (Agv.main_state != clsEnums.MAIN_STATUS.RUN)
                {
                    if (IsTaskCanceled || disposedValue)
                        return;
                    await Task.Delay(1000);
                }
                while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                {
                    if (IsTaskCanceled || disposedValue)
                        return;
                    await Task.Delay(1000);
                }
            }
        }

        private int GetWorkStationTagByNormalPointTag(int tagOfBlockedByPartsReplacing)
        {
            var normalTag = StaMap.GetPointByTagNumber(tagOfBlockedByPartsReplacing);
            var workstationPt = normalTag.Target.Keys.Select(index => StaMap.GetPointByIndex(index)).FirstOrDefault(pt => pt.StationType != STATION_TYPE.Normal);
            if (workstationPt != null)
            {
                return workstationPt.TagNumber;
            }
            else
                return -1;
        }

        protected virtual async Task<bool> WaitAGVReachNexCheckPoint(MapPoint nextCheckPoint, List<MapPoint> nextPath, CancellationToken token)
        {
            bool _waitMovePauseResume = movePause;

            logger.Warn($"[WaitAGVReachNexCheckPoint] WAIT {Agv.Name} Reach-{nextCheckPoint.TagNumber}");
            bool _remainPathConflic = false;
            bool _isAGVAlreadyGoal = IsAGVReachGoal(nextCheckPoint.TagNumber);
            if (_isAGVAlreadyGoal && _waitMovePauseResume)
            {
                await WaitPauseResume();
                return true;
            }
            while (!IsAGVReachGoal(nextCheckPoint.TagNumber))
            {
                if (_waitMovePauseResume || movePause)
                {
                    await WaitPauseResume();
                    return true;
                }

                await Task.Delay(10).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();
                if (Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                {
                    TrafficWaitingState.SetStatusNoWaiting();
                    throw new OperationCanceledException();
                }


                //bool IsRemainPathConflic = _IsRemainPathConflic();
                //if (IsRemainPathConflic != _remainPathConflic)
                //{
                //    if (IsRemainPathConflic)
                //    {
                //        LOG.WARN($"請求 {Agv.Name} 減速");
                //        TrafficWaitingState.SetDisplayMessage("交管請求減速...");
                //        Agv.SpeedSlowRequest();
                //    }
                //    else
                //    {
                //        LOG.WARN($"請求 {Agv.Name} 速度恢復");
                //        TrafficWaitingState.SetStatusNoWaiting();
                //        Agv.SpeedRecovertRequest();
                //    }
                //}
                //_remainPathConflic = IsRemainPathConflic;
            }
            bool _IsRemainPathConflic()
            {
                var indexOfAGV = nextPath.FindIndex(pt => pt.TagNumber == Agv.currentMapPoint.TagNumber);
                //0.1.2.3.4
                return VMSystem.TrafficControl.Tools.CalculatePathInterferenceByAGVGeometry(nextPath.Skip(indexOfAGV + 1), this.Agv, out var conflicAGVList);
            }
            return true;

            async Task WaitPauseResume()
            {
                while (movePause)
                {
                    await Task.Delay(100);
                    TrafficWaitingState.SetDisplayMessage("Pause...");
                }
                _previsousTrajectorySendToAGV.Clear();
            }
        }

        protected virtual IEnumerable<MapPoint> GetNextPath(clsPathInfo optimzedPathInfo, int agvCurrentTag, out bool isNexPathHasEQReplacingParts, out int TagOfBlockedByPartsReplace, int pointNum = 3)
        {
            isNexPathHasEQReplacingParts = false;
            TagOfBlockedByPartsReplace = -1;
            if (pointNum == -1)
            {
                return optimzedPathInfo.stations;
            }
            else
            {
                //0 1 2 3 4 
                var indexOfTagInFrontReplacingEQ = optimzedPathInfo.tags.FindIndex(tag => Dispatch.DispatchCenter.TagListOfInFrontOfPartsReplacingWorkstation.Contains(tag));

                isNexPathHasEQReplacingParts = indexOfTagInFrontReplacingEQ != -1;
                TagOfBlockedByPartsReplace = isNexPathHasEQReplacingParts ? optimzedPathInfo.tags[indexOfTagInFrontReplacingEQ] : -1;
                var _validstations = indexOfTagInFrontReplacingEQ == -1 ? optimzedPathInfo.stations : optimzedPathInfo.stations.Take(indexOfTagInFrontReplacingEQ).ToList();
                var index = _validstations.GetTagCollection().ToList().IndexOf(agvCurrentTag);
                var output = new List<MapPoint>();

                bool _IsSubGoalIsDestine()
                {
                    return (index + pointNum) >= _validstations.Count;
                }
                if (_IsSubGoalIsDestine())
                {
                    output = _validstations;
                }
                else
                {
                    //0 , 1, 2 ,3
                    while (output.Count == 0 && !_IsSubGoalIsDestine())
                    {
                        var subPath = _validstations.Skip(index).Take(pointNum).ToList();
                        if (subPath.Last().IsVirtualPoint)
                        {
                            pointNum += 1;
                            continue;
                        }
                        else
                        {
                            output = subPath.ToList();
                            break;
                        }
                    }
                    if (output.Count == 0)
                    {
                        output = _validstations;
                    }
                }

                return output;
            }
        }
        public override List<List<MapPoint>> GenSequenceTaskByTrafficCheckPoints(List<MapPoint> stations)
        {
            var trafficeControlPtNum = stations.Count() / 3;
            List<int> indexList = new List<int>();
            // 遍歷陣列，每隔三個元素取出一個
            for (int i = 0; i < stations.Count(); i += 3)
            {
                Console.WriteLine($"Index: {i}, Value: {stations[i]}");
                indexList.Add(i);
            }
            // 確保最後一個元素的索引被取出
            if ((stations.Count() - 1) % 3 != 0)
            {
                int lastIndex = stations.Count() - 1;
                indexList.Add(lastIndex);

            }

            var sequences = indexList.Select(index => stations.Take(index + 1).ToList());
            return sequences.ToList();

        }

        protected virtual PathFinderOption pathFinderOptionOfOptimzed { get; } = new PathFinderOption()
        {
            OnlyNormalPoint = true,
            ContainElevatorPoint = false
        };
        internal override bool CheckCargoStatus(out ALARMS alarmCode)
        {
            alarmCode = ALARMS.NONE;
            return true;
        }
        private clsPathInfo CalculateOptimizedPath(int startTag, bool justOptimePath, List<int> additionContrainTags = null)
        {
            PathFinder _pathFinder = new PathFinder();
            MapPoint destinPoint = StaMap.GetPointByTagNumber(this.DestineTag);
            MapPoint startPoint = StaMap.GetPointByTagNumber(startTag);

            List<int> ConstrainTags = new List<int>();
            ConstrainTags = StaMap.RegistDictionary.Where(kp => kp.Value.RegisterAGVName != Agv.Name).Select(kp => kp.Key).ToList();
            ConstrainTags.AddRange(PassedTags);

            List<int> blockedTagsByEqMaintaining = TryGetBlockedTagByEQMaintainFromAGVS().GetAwaiter().GetResult();

            if (additionContrainTags != null)
            {
                ConstrainTags.AddRange(additionContrainTags);
            }

            List<int> _optiPathConstrain = new List<int>();

            var otherAGVTags = VMSManager.AllAGV.FilterOutAGVFromCollection(this.Agv).Select(agv => agv.states.Last_Visited_Node).ToList();

            ConstrainTags.AddRange(blockedTagsByEqMaintaining);
            ConstrainTags = ConstrainTags.Where(tag => tag != startTag && !PassedTags.Contains(tag)).ToList();
            pathFinderOptionOfOptimzed.ConstrainTags = justOptimePath ? otherAGVTags : ConstrainTags;
            clsPathInfo pathPlanResult = _pathFinder.FindShortestPath(StaMap.Map, startPoint, destinPoint, pathFinderOptionOfOptimzed);
            return pathPlanResult;

        }

    }
}

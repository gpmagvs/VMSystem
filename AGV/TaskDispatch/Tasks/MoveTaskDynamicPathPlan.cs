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
using AGVSystemCommonNet6.Log;
using VMSystem.TrafficControl;
using System.Runtime.CompilerServices;
using System.Diagnostics.Eventing.Reader;  // 需要引用System.Numerics向量庫

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveTaskDynamicPathPlan : MoveTask
    {
        public override VehicleMovementStage Stage => VehicleMovementStage.Traveling;
        public MoveTaskDynamicPathPlan() : base()
        {

        }
        public MoveTaskDynamicPathPlan(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {


        }

        private clsPathInfo CalculateOptimizedPath(int startTag, bool justOptimePath, List<int> additionContrainTags = null)
        {
            PathFinder _pathFinder = new PathFinder();
            MapPoint destinPoint = StaMap.GetPointByTagNumber(this.DestineTag);
            MapPoint startPoint = StaMap.GetPointByTagNumber(startTag);
            var option = new PathFinderOption()
            {
                OnlyNormalPoint = true,
                ConstrainTags = justOptimePath ? new List<int>() : StaMap.RegistDictionary.Where(kp => kp.Value.RegisterAGVName != Agv.Name).Select(kp => kp.Key).ToList(),
            };
            option.ConstrainTags.AddRange(PassedTags);

            List<int> blockedTagsByEqMaintaining = TryGetBlockedTagByEQMaintainFromAGVS().GetAwaiter().GetResult();

            if (additionContrainTags != null)
            {
                option.ConstrainTags.AddRange(additionContrainTags);
            }
            option.ConstrainTags.AddRange(blockedTagsByEqMaintaining);
            option.ConstrainTags = option.ConstrainTags.Where(tag => tag != startTag && !PassedTags.Contains(tag)).ToList();
            clsPathInfo pathPlanResult = _pathFinder.FindShortestPath(StaMap.Map, startPoint, destinPoint, option);
            return pathPlanResult;

        }

        private MapPoint GetAGVCurrentPoint()
        {
            return StaMap.GetPointByTagNumber(Agv.states.Last_Visited_Node);
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
                    List<clsMapPoint> _previsousTrajectorySendToAGV = new List<clsMapPoint>();
                    int pointNum = AGVSystemCommonNet6.Configuration.AGVSConfigulator.SysConfigs.TaskControlConfigs.SegmentTrajectoryPointNum;
                    int pathStartTagToCal = Agv.states.Last_Visited_Node;
                    int _lastFinalEndTag = -1;
                    List<MapPoint> _lastNextPath = new List<MapPoint>();
                    while (!IsAGVReachGoal(DestineTag, checkTheta: true) || _sequenceIndex == 0)
                    {
                        if (token.IsCancellationRequested)
                            token.ThrowIfCancellationRequested();
                        if (Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                        {
                            TrafficWaitingState.SetStatusNoWaiting();
                            break;
                        }
                        bool _findPath = false;
                        clsPathInfo optimzePath = null;
                        while ((optimzePath = CalculateOptimizedPath(pathStartTagToCal, _sequenceIndex == 0)) == null)
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



                        List<MapPoint> nextPath = GetNextPath(optimzePath, pathStartTagToCal, pointNum);

                        if (_lastNextPath.Count != 0 && !nextPath.Contains(_lastNextPath.Last()))
                        {
                            LOG.Critical($"Replan . Use other path");
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
                            await Task.Delay(1000);
                            continue;
                        }
                        if (_waitingInterference)
                        {
                            pathStartTagToCal = Agv.states.Last_Visited_Node;
                            continue;
                        }

                        TrafficWaitingState.SetStatusNoWaiting();
                        _previsousTrajectorySendToAGV.AddRange(PathFinder.GetTrajectory(StaMap.Map.Name, nextPath));
                        _previsousTrajectorySendToAGV = _previsousTrajectorySendToAGV.DistinctBy(pt => pt.Point_ID).ToList();


                        clsTaskDownloadData _taskDownloadData = this.TaskDonwloadToAGV.Clone();
                        _taskDownloadData.Task_Sequence = _sequenceIndex;
                        _taskDownloadData.Task_Simplex = task_simplex + "-" + _sequenceIndex;
                        _taskDownloadData.Trajectory = _previsousTrajectorySendToAGV.ToArray();

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


                        LOG.Critical($"Send Task To AGV when AGV last visited Tag = {Agv.states.Last_Visited_Node}");
                        var _result = await _DispatchTaskToAGV(_taskDownloadData);
                        if (_result.ReturnCode != TASK_DOWNLOAD_RETURN_CODES.OK)
                        {
                            if (OnTaskDownloadToAGVButAGVRejected != null)
                                OnTaskDownloadToAGVButAGVRejected(_result.ReturnCode.ToAGVSAlarmCode());
                            return;
                        }
                        StaMap.RegistPoint(Agv.Name, MoveTaskEvent.AGVRequestState.NextSequenceTaskRemainTagList, out string ErrorMessage);

                        var agvLastVisitNodeIndex = nextPath.FindIndex(pt => pt.TagNumber == Agv.states.Last_Visited_Node);
                        var nextCheckPoint = pointNum == -1 ?
                                            MoveTaskEvent.AGVRequestState.NextSequenceTaskTrajectory.Last() :
                                            agvLastVisitNodeIndex == -1 || agvLastVisitNodeIndex + 1 >= nextPath.Count ?
                                            nextPath.First() : nextPath[nextPath.Count - 2];
                        //1,2,3,4,5,6
                        LOG.INFO($"[WaitAGVReachGoal] Wait {Agv.Name} Reach-{nextCheckPoint.TagNumber}");
                        while (!IsAGVReachGoal(nextCheckPoint.TagNumber))
                        {
                            await Task.Delay(10).ConfigureAwait(false);

                            if (token.IsCancellationRequested)
                                token.ThrowIfCancellationRequested();
                            if (Agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                TrafficWaitingState.SetStatusNoWaiting();
                                return;
                            }
                        }
                        pathStartTagToCal = nextCheckPoint.TagNumber;
                        _sequenceIndex += 1;
                        await Task.Delay(10);

                    }

                    List<MapPoint> GetNextPath(clsPathInfo optimzedPathInfo, int agvCurrentTag, int pointNum = 3)
                    {
                        if (pointNum == -1)
                        {
                            return optimzedPathInfo.stations;
                        }
                        else
                        {
                            var index = optimzedPathInfo.stations.GetTagCollection().ToList().IndexOf(agvCurrentTag);
                            var output = new List<MapPoint>();

                            bool _IsSubGoalIsDestine()
                            {
                                return (index + pointNum) >= optimzedPathInfo.stations.Count;
                            }
                            if (_IsSubGoalIsDestine())
                            {
                                output = optimzedPathInfo.stations;
                            }
                            else
                            {
                                //0 , 1, 2 ,3
                                while (output.Count == 0 && !_IsSubGoalIsDestine())
                                {
                                    var subPath = optimzedPathInfo.stations.Skip(index).Take(pointNum).ToList();
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
                            }

                            return output;
                        }
                    }

                    MapPoint GetGoalStationWhenNonNormalTaskExecute()
                    {
                        MapPoint _targetStation = new MapPoint();
                        if (this.Stage == VehicleMovementStage.Traveling_To_Source)
                            _targetStation = StaMap.GetPointByTagNumber(this.OrderData.From_Station_Tag);
                        else if (this.Stage == VehicleMovementStage.Traveling_To_Destine)
                            _targetStation = StaMap.GetPointByTagNumber(this.OrderData.To_Station_Tag);
                        return _targetStation;
                    }

                    void SettingParkAngle(ref clsTaskDownloadData _taskDownloadData)
                    {
                        double theta = 0;
                        bool isNextStopIsFinal = _taskDownloadData.ExecutingTrajecory.Last().Point_ID == this.DestineTag;

                        if (isNextStopIsFinal && (OrderData.Action == ACTION_TYPE.None || OrderData.Action == ACTION_TYPE.ExchangeBattery))
                        {
                            LOG.WARN($"Next path goal is destine, park");
                            return;
                        }

                        if (_taskDownloadData.Trajectory.Length < 2)
                        {

                            if (OrderData.Action == ACTION_TYPE.None)
                                _taskDownloadData.Trajectory.Last().Theta = _taskDownloadData.Trajectory.Last().Theta;
                            else
                            {
                                MapPoint _targetStation = GetGoalStationWhenNonNormalTaskExecute();
                                _taskDownloadData.Trajectory.Last().Theta = _targetStation.Direction_Secondary_Point;

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
                                MapPoint _front_targetStation_point = StaMap.GetPointByIndex(_targetStation.Target.Keys.First());

                                if (_front_targetStation_point.TagNumber == lastPoint.Point_ID)
                                {
                                    theta = FinalStopTheta;
                                    _taskDownloadData.Trajectory.Last().Theta = theta;
                                    return;
                                }
                            }

                            _taskDownloadData.Trajectory.Last().Theta = Tools.NavigationTools.CalculationForwardAngle(lastSecondPt, lastPt); ;
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    LOG.WARN($"任務-{OrderData.ActionName}(ID:{OrderData.TaskName} )已取消");

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
    }
}

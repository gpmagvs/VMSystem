using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using AGVSystemCommonNet6.Notify;
using System.Threading.Tasks;
using VMSystem.Dispatch;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.AGV.TaskDispatch.Tasks.MoveTaskDynamicPathPlanV2;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class NavigateToGoalTask : TaskBase
    {
        public NavigateToGoalTask()
        {

        }

        public NavigateToGoalTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling;

        public override ACTION_TYPE ActionType => ACTION_TYPE.None;
        public MapPoint NextPathExtendCheckPoint { get; set; } = new MapPoint();
        MapPoint OrderGoalPoint { get; set; } = new MapPoint();
        public override async Task SendTaskToAGV()
        {
            try
            {
                OrderGoalPoint = this.OrderData.GetFinalMapPoint(this.Agv, this.Stage);
                DestineTag = OrderGoalPoint.TagNumber;
                MapPoint destinePoint = StaMap.GetPointByTagNumber(DestineTag);
                Agv.OnMapPointChanged += Agv_OnMapPointChanged;
                await Navigation(Agv.currentMapPoint, OrderGoalPoint, new List<MapPoint>());
                Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
                NotifyServiceHelper.INFO($"Navigating-{OrderData.TaskName} Finish.");
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private async Task Navigation(MapPoint currentPoint, MapPoint goalPoint, List<MapPoint> passedMapPoints, bool isAvoiding = false)
        {

            IEnumerable<MapPoint> pathResponse = GetNextNavigationPath(currentPoint, goalPoint, out IAGV conflicAGV, out MapRectangle conflicRegion);
            bool isPathConflic()
            {
                return passedMapPoints.Any() && passedMapPoints.Last().TagNumber == pathResponse.Last().TagNumber;
            }

            if (pathResponse == null || !pathResponse.Any() || isPathConflic())
            {
                if (!IsTaskExecutable())
                    throw new TaskCanceledException();

                await Task.Delay(100);

                if (conflicRegion != null && conflicAGV != null)
                {
                    UpdateStateDisplayMessage($"[{conflicRegion.ToString()}] Conflic To [{conflicAGV.Name}]");
                    Agv.NavigationState.currentConflicToAGV = conflicAGV;
                    Agv.NavigationState.CurrentConflicRegion = conflicRegion;
                    Agv.NavigationState.IsWaitingConflicSolve = true;
                }

                if (Agv.NavigationState.AvoidActionState.IsAvoidRaising)
                {
                    await AvoidActionNavigation();
                    UpdateStateDisplayMessage($"Avoid Action Done...");
                    await Task.Delay(1000);
                    passedMapPoints = new List<MapPoint>();
                    await Navigation(Agv.currentMapPoint, goalPoint, passedMapPoints);
                }
                else
                    await Navigation(Agv.currentMapPoint, goalPoint, passedMapPoints);
            }
            //prepare trajectory

            Agv.NavigationState.currentConflicToAGV = null;
            Agv.NavigationState.CurrentConflicRegion = null;
            Agv.NavigationState.IsWaitingConflicSolve = false;
            List<MapPoint> _SendToPoints = new List<MapPoint>();
            _SendToPoints.AddRange(passedMapPoints);
            _SendToPoints.AddRange(pathResponse.ToList());
            _SendToPoints = _SendToPoints.DistinctBy(pt => pt.TagNumber).ToList();

            double theta = SettingStopAngle(ref _SendToPoints);

            if (_SendToPoints.Last().TagNumber == goalPoint.TagNumber && passedMapPoints.Any() && passedMapPoints.Last()?.TagNumber == goalPoint.TagNumber)
            {
                NotifyServiceHelper.INFO($"{Agv.Name} Reach Destine {goalPoint.Graph.Display}");
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                return;
            }

            UpdateMoveStateMessage($"Regist Point...");
            if (!StaMap.RegistPoint(Agv.Name, pathResponse, out string errmsg))
            {
                await StaMap.UnRegistPointsOfAGVRegisted(Agv);
                UpdateMoveStateMessage($"Regist Point.Fail..{errmsg}");
                await Task.Delay(1000);
                await Navigation(Agv.currentMapPoint, goalPoint, passedMapPoints);
            }

            UpdateMoveStateMessage($"Regist Point.Done..");
            await Task.Delay(200);

            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
            //create clsTaskDownloadData and send to AGV
            (TaskDownloadRequestResponse agvResponse, clsMapPoint[] trajectory)= await Agv.TaskExecuter.TaskDownload(this, new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                CST = new clsCST[] { new clsCST { CST_ID = OrderData.Carrier_ID, CST_Type = OrderData.CST_TYPE == 200 ? CST_TYPE.Tray : CST_TYPE.Rack } },
                Task_Name = OrderData.TaskName,
                Destination = goalPoint.TagNumber,
                Height = OrderData.Height,
                Trajectory = PathFinder.GetTrajectory(_SendToPoints),
            });

            //check response from AGV and update passedMapPoints
            if (agvResponse.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
            {
                MapPoint _subGoal = _SendToPoints.Last();
                NextPathExtendCheckPoint = TryGetNextPathExtendCheckPoint(_SendToPoints);
                UpdateMoveStateMessage($"Go To {_subGoal.Graph.Display}");
                Agv.NavigationState.UpdateNavigationPoints(pathResponse);
                passedMapPoints = _SendToPoints.Clone();
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
                if (Agv.currentMapPoint.TagNumber != goalPoint.TagNumber)
                {
                    bool isAgvReachSubGoal = Agv.currentMapPoint.TagNumber == _subGoal.TagNumber;
                    if (isAgvReachSubGoal || Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                    {
                        await Navigation(Agv.currentMapPoint, goalPoint, passedMapPoints);
                    }
                    else //在途中停下，表示AGVCycle Stop 或 Path Expand
                    {
                        NotifyServiceHelper.WARNING($"{Agv.Name} Cycle Stop At {Agv.currentMapPoint.Graph.Display}");
                        return;
                    }
                }
                else
                {
                    UpdateMoveStateMessage($"Reach Goal !");
                    return;
                }
            }
            else
            {
                throw new TaskCanceledException();
            }

        }

        private async Task AvoidActionNavigation()
        {
            Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
            Agv.NavigationState.IsWaitingConflicSolve = false;
            Agv.NavigationState.AvoidActionState.IsAvoidRaising = false;
            Agv.NavigationState.StartWaitConflicSolveTime = DateTime.Now;
            Agv.NavigationState.IsWaitingConflicSolve = false;
            await Task.Delay(400);
            await Agv.TaskExecuter.TaskCycleStop(OrderData.TaskName);
            await StaMap.UnRegistPointsOfAGVRegisted(Agv);
            NavigateToGoalTask avoidTask = new NavigateToGoalTask(Agv, new clsTaskDto
            {
                TaskName = OrderData.TaskName,
                DesignatedAGVName = OrderData.DesignatedAGVName,
                Action = ACTION_TYPE.None,
                To_Station = Agv.NavigationState.AvoidActionState.AvoidPt.TagNumber + "",
            });
            NotifyServiceHelper.INFO($"{Agv.Name} Avoid Path Request Raising.");
            await avoidTask.SendTaskToAGV();
            Agv.OnMapPointChanged += Agv_OnMapPointChanged;
        }

        /// <summary>
        /// 設定停車角度
        /// </summary>
        /// <param name="sendToPoints"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        private double SettingStopAngle(ref List<MapPoint> sendToPoints)
        {
            double theta = 0;

            if (OrderData.Action != ACTION_TYPE.None && sendToPoints.Count > 1)
            {
                MapPoint nextGoalPoint = sendToPoints.Last();
                MapPoint workStationPoint = GetWorkStationPoint();
                bool isCurrentGoalIsDesinte = nextGoalPoint.TagNumber == DestineTag;

                if (isCurrentGoalIsDesinte) //下一次停車點是目的地了， 設定停車角度
                {
                    theta = Tools.CalculationForwardAngle(nextGoalPoint, workStationPoint);
                }
                else
                {
                    int lengthOfPath = sendToPoints.Count;
                    theta = Tools.CalculationForwardAngle(sendToPoints[lengthOfPath - 2], nextGoalPoint);
                }

            }
            else
            {
                theta = sendToPoints.Last().Direction;//nothing change ^_^
            }

            sendToPoints.Last().Direction = theta;
            return theta;

            MapPoint GetWorkStationPoint()
            {
                if (Stage == VehicleMovementStage.Traveling_To_Source)
                    return StaMap.GetPointByTagNumber(OrderData.From_Station_Tag);
                else
                {
                    return StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);

                }
            }
        }


        private IEnumerable<MapPoint> GetNextNavigationPath(MapPoint currentPoint, MapPoint goalPoint, out IAGV conflicAGV, out MapRectangle conflicRegion)
        {
            conflicRegion = null;
            conflicAGV = null;
            try
            {
                PathFinder pathFinder = new PathFinder();
                clsPathInfo pathInfoWrapper = pathFinder.FindShortestPath(currentPoint.TagNumber, goalPoint.TagNumber, new PathFinderOption
                {
                    Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
                    OnlyNormalPoint = true,

                });
                if (pathInfoWrapper == null || !pathInfoWrapper.tags.Any())
                    return null;

                bool TryFindConflicAGV(out IAGV conflicAGV, out MapRectangle confliRectangle)
                {
                    confliRectangle = null;
                    VehicleNavigationState navigationElevator = Agv.NavigationState;
                    navigationElevator.UpdateNavigationPointsForPathCalculation(pathInfoWrapper.stations);
                    List<MapRectangle> rectanglesForElevate = navigationElevator.NextPathOccupyRegionsForPathCalculation;
                    Dictionary<IAGV, MapRectangle> conflicStore = OtherAGV.ToDictionary(agv => agv, agv => rectanglesForElevate.FirstOrDefault(reg => agv.NavigationState.NextPathOccupyRegions.Any(_reg => _reg.IsIntersectionTo(reg))));
                    KeyValuePair<IAGV, MapRectangle> conflicSt = conflicStore.FirstOrDefault(agv => agv.Value != null);
                    conflicAGV = conflicSt.Key;
                    confliRectangle = conflicSt.Value;
                    return conflicAGV != null;
                }
                if (TryFindConflicAGV(out conflicAGV, out MapRectangle _conflicRegion))
                {
                    conflicRegion = _conflicRegion;
                    int conflicStartIndex = pathInfoWrapper.stations.FindIndex(pt => pt.TagNumber == _conflicRegion.StartPoint.TagNumber);
                    IEnumerable<MapPoint> pathToNavigate = pathInfoWrapper.stations.Take(conflicStartIndex);
                    MapPoint lastNonVirtualPoint = pathToNavigate.LastOrDefault(pt => !pt.IsVirtualPoint);
                    int lastNonVirtualPointINdex = pathToNavigate.ToList().FindIndex(pt => pt.TagNumber == lastNonVirtualPoint.TagNumber);
                    return pathToNavigate.Take(lastNonVirtualPointINdex + 1);
                }
                else
                    return pathInfoWrapper.stations;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private MapPoint TryGetNextPathExtendCheckPoint(List<MapPoint> sendToPoints)
        {
            try
            {
                if (!sendToPoints.Any() || sendToPoints.Count <= 2 || sendToPoints.Last().TagNumber == DestineTag)
                    return new MapPoint();
                return sendToPoints[sendToPoints.Count - 2];//0,1,2
            }
            catch (Exception ex)
            {
                return new MapPoint();
            }
        }

        public override bool IsAGVReachDestine
        {
            get
            {
                return Agv.currentMapPoint.TagNumber == DestineTag;
            }
        }

        private void Agv_OnMapPointChanged(object? sender, int tag)
        {
            MapPoint point = StaMap.GetPointByTagNumber(tag);
            Agv.NavigationState.CurrentMapPoint = point;
            if (point.TagNumber == NextPathExtendCheckPoint.TagNumber)
            {
                NotifyServiceHelper.INFO($"{Agv.Name} Reach {NextPathExtendCheckPoint.Graph.Display} : Get Next Extend Path Now.");
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Set();
            }
        }
        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
        }

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }

        public override void CancelTask()
        {
            base.CancelTask();
        }
    }
}

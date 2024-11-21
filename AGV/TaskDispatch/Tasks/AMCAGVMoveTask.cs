using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DevicesControl;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using System.Collections.Generic;
using System.Linq;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class AMCAGVMoveTask : MoveTaskDynamicPathPlan
    {

        public override bool IsAGVReachDestine
        {
            get
            {
                if (OrderData.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Measure)
                {
                    return StaMap.Map.Bays[OrderData.To_Station].InPoint == Agv.currentMapPoint.Graph.Display;
                }
                else if (OrderData.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.ExchangeBattery)
                {
                    var exchangeStation = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                    return Agv.currentMapPoint.TagNumber == exchangeStation.TagOfInPoint;
                }
                else
                    return Agv.states.Last_Visited_Node == OrderData.To_Station_Tag;
            }
        }
        public override VehicleMovementStage Stage => VehicleMovementStage.Traveling_To_Destine;


        private void Agv_OnMapPointChanged(object? sender, int e)
        {
            var currentPt = Agv.NavigationState.NextNavigtionPoints.FirstOrDefault(p => p.TagNumber == e);
            if (currentPt != null)
            {
                Agv.NavigationState.CurrentMapPoint = currentPt;
                List<int> _NavigationTags = Agv.NavigationState.NextNavigtionPoints.GetTagCollection().ToList();
                var ocupyRegionTags = Agv.NavigationState.NextPathOccupyRegions.SelectMany(rect => new int[] { rect.StartPoint.TagNumber, rect.EndPoint.TagNumber })
                                                         .DistinctBy(tag => tag);

                UpdateMoveStateMessage($"{string.Join("->", ocupyRegionTags)}");
            }
        }

        private ManualResetEvent _waitTaskFinish = new ManualResetEvent(false);

        public enum ELEVATOR_ENTRY_STATUS
        {
            MOVE_TO_ENTRY_PT_OF_ELEVATOR,
            ENTER_ELEVATOR,
            LEAVE_ELEVATOR,
            NO_PASS_ELEVATOR
        }
        public ELEVATOR_ENTRY_STATUS ElevatorStatus { get; set; } = ELEVATOR_ENTRY_STATUS.NO_PASS_ELEVATOR;
        private MapPoint EntryPointOfElevator;

        public AMCAGVMoveTask(IAGV Agv, clsTaskDto orderData, AGVSDbContext agvsDb, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, agvsDb, taskTbModifyLock)
        {
            Agv.OnMapPointChanged += Agv_OnMapPointChanged;
        }

        protected override PathFinderOption pathFinderOptionOfOptimzed => new PathFinderOption
        {
            OnlyNormalPoint = true,
            ContainElevatorPoint = true
        };
        protected override List<MapPoint> GetNextPath(clsPathInfo optimzedPathInfo, int agvCurrentTag, out bool isNexPathHasEQReplacingParts, out int TagOfBlockedByPartsReplace, int pointNum = 3)
        {
            try
            {

                isNexPathHasEQReplacingParts = false;
                TagOfBlockedByPartsReplace = -1;
                var elevatorPoint = optimzedPathInfo.stations.Find(station => station.StationType == STATION_TYPE.Elevator);
                bool IsPathContainElevator = elevatorPoint != null;
                int IndexOfAGVLocation()
                {
                    return optimzedPathInfo.stations.FindIndex(st => st.TagNumber == Agv.currentMapPoint.TagNumber);
                }
                logger.Trace($"IsPathContainElevator=>{IsPathContainElevator},ElevatorStatus :{ElevatorStatus}");
                if (IsPathContainElevator && ElevatorStatus == ELEVATOR_ENTRY_STATUS.NO_PASS_ELEVATOR)
                {
                    _previsousTrajectorySendToAGV.Clear();
                    IEnumerable<MapPoint> pointsOfElevatorEntryAndLeave = elevatorPoint.Target.Keys.Select(index => StaMap.GetPointByIndex(index));
                    EntryPointOfElevator = optimzedPathInfo.stations.First(station => pointsOfElevatorEntryAndLeave.GetTagCollection().Contains(station.TagNumber));
                    var entryPointIndexOfPath = optimzedPathInfo.stations.IndexOf(EntryPointOfElevator);

                    ElevatorStatus = ELEVATOR_ENTRY_STATUS.MOVE_TO_ENTRY_PT_OF_ELEVATOR;
                    //0 1 2 3
                    return optimzedPathInfo.stations.Skip(IndexOfAGVLocation()).Take(entryPointIndexOfPath + 1).ToList();
                }
                else if (ElevatorStatus == ELEVATOR_ENTRY_STATUS.MOVE_TO_ENTRY_PT_OF_ELEVATOR)
                {
                    _previsousTrajectorySendToAGV.Clear();
                    var entryPointIndexOfPath = optimzedPathInfo.stations.IndexOf(EntryPointOfElevator);
                    ElevatorStatus = ELEVATOR_ENTRY_STATUS.ENTER_ELEVATOR;
                    return optimzedPathInfo.stations.Skip(IndexOfAGVLocation()).Take(2).ToList(); // 0. 1 .2.3.4
                }
                else if (ElevatorStatus == ELEVATOR_ENTRY_STATUS.ENTER_ELEVATOR)
                {
                    _previsousTrajectorySendToAGV.Clear();
                    ElevatorStatus = ELEVATOR_ENTRY_STATUS.NO_PASS_ELEVATOR;
                    return optimzedPathInfo.stations.Skip(IndexOfAGVLocation()).Take(optimzedPathInfo.stations.Count - IndexOfAGVLocation()).ToList();
                }
                else
                    return base.GetNextPath(optimzedPathInfo, agvCurrentTag, out bool _, out int _, pointNum).ToList();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                throw ex;
            }
        }
        public ElevatorControl Elevator { get; private set; } = new ElevatorControl();
        protected override async Task<bool> WaitAGVReachNexCheckPoint(MapPoint nextCheckPoint, List<MapPoint> nextPath, CancellationToken token)
        {
            await base.WaitAGVReachNexCheckPoint(nextCheckPoint, nextPath, token);
            await ElevatorTaskControl();
            return true;
        }

        protected override bool IsAGVReachGoal(int goal_id, bool justAlmostReachGoal = false, bool checkTheta = false)
        {
            MapPoint goalPoint = StaMap.GetPointByTagNumber(goal_id);
            double distanceToGoal = goalPoint.CalculateDistance(Agv.states.Coordination);

            if (distanceToGoal > 0.3)
            {
                logger.Warn($"距離終點({goal_id},({goalPoint.X},{goalPoint.Y})) 距離 {distanceToGoal}, 大於確認閥值(0.3m)");
                return false;
            }

            return base.IsAGVReachGoal(goal_id, justAlmostReachGoal, checkTheta);
        }
        private async Task ElevatorTaskControl()
        {
            switch (ElevatorStatus)
            {
                case ELEVATOR_ENTRY_STATUS.MOVE_TO_ENTRY_PT_OF_ELEVATOR:

                    TrafficWaitingState.SetDisplayMessage("等待電梯底抵達當前樓層...");
                    await Elevator.CallElevatorComeAndWait(Agv.currentFloor);
                    TrafficWaitingState.SetDisplayMessage("進入電梯...");
                    break;
                case ELEVATOR_ENTRY_STATUS.ENTER_ELEVATOR:

                    while (Agv.main_state == clsEnums.MAIN_STATUS.RUN)
                    {
                        if (_TaskCancelTokenSource.IsCancellationRequested)
                            return;
                        TrafficWaitingState.SetDisplayMessage($"等待AGV停車於電梯");
                        await Task.Delay(1000);
                    }
                    TrafficWaitingState.SetStatusNoWaiting();
                    int nextFloor = 1;

                    //Random setting floor to simulation
                    while ((nextFloor = new Random(DateTime.Now.Second).Next(1, 6)) == Agv.currentFloor)
                    {
                        if (_TaskCancelTokenSource.IsCancellationRequested)
                            return;
                        await Task.Delay(1000);
                    }
                    TrafficWaitingState.SetDisplayMessage($"等待電梯抵達[{nextFloor}]樓..");
                    await Elevator.GoTo(nextFloor);
                    TrafficWaitingState.SetDisplayMessage($"進入電梯[{nextFloor}]");
                    TrafficWaitingState.SetStatusNoWaiting();
                    Agv.currentFloor = nextFloor;
                    _ = Task.Run(async () =>
                    {
                        while (Agv.currentMapPoint.StationType == STATION_TYPE.Elevator)
                        {
                            if (_TaskCancelTokenSource.IsCancellationRequested)
                                return;
                            TrafficWaitingState.SetDisplayMessage($"等待AGV離開電梯...");
                            await Task.Delay(1000);
                        }
                        TrafficWaitingState.SetStatusNoWaiting();
                        await Elevator.CloseDoor(Agv.currentFloor);
                    });
                    break;
                case ELEVATOR_ENTRY_STATUS.LEAVE_ELEVATOR:
                    break;
                case ELEVATOR_ENTRY_STATUS.NO_PASS_ELEVATOR:
                    break;
                default:
                    break;
            }
        }

        public override (bool continuetask, clsTaskDto task, ALARMS alarmCode, string errorMsg) ActionFinishInvoke()
        {
            _waitTaskFinish.Set();
            return base.ActionFinishInvoke();
        }
        private List<clsTaskDto> SplitOrder(clsTaskDto orderData)
        {
            List<clsTaskDto> splitedOrders = new List<clsTaskDto>();
            int DestineTag = orderData.To_Station_Tag;
            int AGVCurrentTag = Agv.states.Last_Visited_Node;
            PathFinder _pathFinder = new PathFinder();
            clsPathInfo pathFindResult = _pathFinder.FindShortestPathByTagNumber(StaMap.Map, AGVCurrentTag, DestineTag, new PathFinder.PathFinderOption
            {
                OnlyNormalPoint = true
            });
            if (pathFindResult != null)
            {
                List<MapPoint> pathPoints = pathFindResult.stations;
                int pathPointCount = 3;
                if (pathPoints.Count > pathPointCount)
                {
                    for (int i = 0; i < pathPointCount; i++)
                    {
                        List<MapPoint> subPoints = pathPoints.Skip(pathPointCount * i).Take(pathPointCount).ToList();
                        if (subPoints.Count > 0)
                            splitedOrders.Add(cloneOrderWithDestineTag(orderData, subPoints.Last().TagNumber));
                    }
                }
                else
                {
                    splitedOrders.Add(cloneOrderWithDestineTag(orderData, pathPoints.Last().TagNumber));
                }
            }

            return splitedOrders;

            clsTaskDto cloneOrderWithDestineTag(clsTaskDto oriOrder, int destine)
            {
                clsTaskDto subOrder = oriOrder.Clone();
                subOrder.To_Station = destine + "";
                return subOrder;
            }
        }
    }
}

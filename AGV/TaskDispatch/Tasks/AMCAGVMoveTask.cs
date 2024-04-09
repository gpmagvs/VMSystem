using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class AMCAGVMoveTask : MoveTaskDynamicPathPlan
    {
        public AMCAGVMoveTask()
        {
        }
        public override bool IsAGVReachDestine
        {
            get
            {
                return Agv.states.Last_Visited_Node == OrderData.To_Station_Tag;
            }
        }
        public AMCAGVMoveTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }
        private ManualResetEvent _waitTaskFinish = new ManualResetEvent(false);
        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            List<clsTaskDto> subsOrders = SplitOrder(OrderData);
            List<MoveTaskDynamicPathPlan> moveTasksCollection = subsOrders.Select(subOrder => new MoveTaskDynamicPathPlan(Agv, subOrder)
            {
                TaskName = OrderData.TaskName
            }).ToList();
            foreach (MoveTaskDynamicPathPlan _moveTask in moveTasksCollection)
            {
                _waitTaskFinish.Reset();
                (bool confirmed, ALARMS alarm_code) _result = await _moveTask.DistpatchToAGV();
                _waitTaskFinish.WaitOne();
                LOG.INFO($"Task-{_moveTask.TaskSimple} confirm:{_result.confirmed}, Alarm Code:{_result.alarm_code}(AGV Locate :{Agv.states.Last_Visited_Node})");

            }
            return (true, ALARMS.NONE);
        }
        public override void ActionFinishInvoke()
        {
            _waitTaskFinish.Set();
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

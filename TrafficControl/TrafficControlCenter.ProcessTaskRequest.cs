using AGVSystemCommonNet6.MAP;
using System.Linq;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl.Solvers;
using VMSystem.VMS;

namespace VMSystem.TrafficControl
{
    public partial class TrafficControlCenter
    {

        /// <summary>
        /// Key:需要被交管移動的AGV,Value
        /// </summary>
        private static async Task<clsMoveTaskEvent> ProcessTaskRequest(clsMoveTaskEvent moveTaskRequset)
        {
            return await Task.Run(() =>
            {
                IAGV SendRequestAGV = moveTaskRequset.AGVRequestState.Agv;
                Dictionary<int, clsPointRegistInfo> RegistedPointsByOtherAGV = StaMap.RegistDictionary.Where(keypair => keypair.Value.RegisterAGVName != SendRequestAGV.Name).ToList().ToDictionary(kp => kp.Key, kp => kp.Value);
                ///1- 嘗試將阻擋車輛移走
                ///
                List<IAGV> needYieldWayAGVList = TryYieldAGVAway(moveTaskRequset, SendRequestAGV);

                moveTaskRequset.TrafficResponse.YieldWayAGVList = needYieldWayAGVList;

                return moveTaskRequset;
            });
        }

        private static List<IAGV> TryYieldAGVAway(clsMoveTaskEvent moveTaskRequset, IAGV SendRequestAGV)
        {
            var othersAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(SendRequestAGV);
            var idlingAGVList = othersAGV.Where(agv => agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING);
            var needYieldWayAGVList = idlingAGVList.Where(idlingAGV => moveTaskRequset.AGVRequestState.OptimizedToDestineTrajectoryTagList.Contains(idlingAGV.states.Last_Visited_Node)).ToList();
            foreach (var idlingAGV in needYieldWayAGVList)
            {
                YieldWayEventCommander moveAGVAwayCommander = new TrafficEventCommanderFactory().CreateYieldWayEventCommander(SendRequestAGV, idlingAGV);
                moveAGVAwayCommander.StartSolve();
            }
            return needYieldWayAGVList;
        }

        private static async Task SolveRegistedPointsTrafficEvents(IAGV? sendRequestAGV, Dictionary<int, clsPointRegistInfo> registedPointsByOtherAGV)
        {
            await Task.Delay(1);
            await Task.Run(async () =>
            {
                var BlockedPointAGVMap = registedPointsByOtherAGV.ToDictionary(kp => kp.Key, kp => VMSManager.GetAGVByName(kp.Value.RegisterAGVName));

                TrafficEventCommanderFactory factory = new TrafficEventCommanderFactory();
                List<IAGV> blockedPointAGVList = BlockedPointAGVMap.Values.Distinct().ToList();

                var solovers = blockedPointAGVList.Select(registConflicPointAGV => factory.CreateTrafficEventSolover(sendRequestAGV, registConflicPointAGV)).ToList();

                foreach (var solver in solovers)
                {
                    await solver.StartSolve();
                }

                if (solovers.Any(sol => sol.TRAFFIC_EVENT == ETRAFFIC_EVENT.FollowCar))
                    return;
                var moveTaskState = sendRequestAGV.taskDispatchModule.OrderHandler.RunningTask.MoveTaskEvent;
                moveTaskState.TrafficResponse.ConfirmResult = clsMoveTaskEvent.GOTO_NEXT_GOAL_CONFIRM_RESULT.ACCEPTED_GOTO_NEXT_GOAL;
                moveTaskState.TrafficResponse.Wait_Traffic_Control_Finish_ResetEvent.Set();
            });
        }

        /// <summary>
        /// 下一段路徑是否有包含被其他AGV註冊的點位
        /// </summary>
        /// <param name="requestMoveEvent"></param>
        /// <param name="registedPoints"></param>
        /// <param name="blockedTagList"></param>
        /// <returns></returns>
        private static bool IsNextPathBlocked(clsMoveTaskEvent requestMoveEvent, Dictionary<int, clsPointRegistInfo> registedPoints, out List<int> blockedTagList)
        {
            blockedTagList = new List<int>();
            var agv_current_tag = requestMoveEvent.AGVRequestState.Agv.states.Last_Visited_Node;
            var nextPathWholeTags = requestMoveEvent.AGVRequestState.NextSequenceTaskTrajectory.GetTagCollection();
            var nextMoveTags = nextPathWholeTags.Skip(nextPathWholeTags.ToList().IndexOf(agv_current_tag) + 1);

            blockedTagList = nextMoveTags.Where(tag => registedPoints.Keys.Contains(tag)).ToList();
            return blockedTagList.Count != 0;
        }
    }
}

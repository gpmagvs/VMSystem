using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using System.Linq;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using static VMSystem.TrafficControl.Solvers.clsSolverResult;

namespace VMSystem.Dispatch
{
    public class ConflicSolveResult
    {
        public CONFLIC_ACTION NextAction { get; }

        public enum CONFLIC_ACTION
        {
            STOP_AND_WAIT,
            REPLAN,
            ACCEPT_GO
        }

        public ConflicSolveResult(CONFLIC_ACTION action)
        {
            this.NextAction = action;
        }
    }
    public class NavigationPriorityHelper
    {



        /// <summary>
        /// 處理終點與其他車輛有衝突的情境
        /// </summary>
        /// <param name="vehicle"></param>
        /// <param name="confliAGVList"></param>
        /// <param name="finalMapPoint"></param>
        internal async Task<ConflicSolveResult> GetPriorityByBecauseDestineConflicAsync(IAGV vehicle, List<IAGV> confliAGVList, MapPoint finalMapPoint)
        {
            Dictionary<IAGV, ACTION_TYPE> conflicAGVCurrentActions = confliAGVList.ToDictionary(agv => agv, agv => agv.CurrentRunningTask().ActionType);
            Dictionary<IAGV, ACTION_TYPE> lduldActionRunningsVehicles = conflicAGVCurrentActions.Where(pair => IsVehicleReadyToUDULD(pair.Key))
                                                                                                .ToDictionary(kp => kp.Key, kp => kp.Value);

            bool isConflicWithUDLUDVehicles = lduldActionRunningsVehicles.Any();
            if (isConflicWithUDLUDVehicles)//與在進行取放貨作業的車輛衝突
            {
                IEnumerable<MapPoint> WorkStations = lduldActionRunningsVehicles.Keys.Select(agv => StaMap.GetPointByTagNumber(agv.CurrentRunningTask().OrderData.To_Station_Tag));
                IEnumerable<MapPoint> entryPointsOfWorkStations = WorkStations.SelectMany(pt => pt.TargetNormalPoints());

                bool isAllVehicleReadyToWork = lduldActionRunningsVehicles.Keys.All(v => _isVehicleReadyToEntryWorkStation(v, out _));

                if (isAllVehicleReadyToWork)
                {
                    vehicle.NavigationState.ConflicAction = ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT;

                    List<Task<bool>> AllWaitLeaveWorkStationTasks = new List<Task<bool>>();

                    AllWaitLeaveWorkStationTasks = lduldActionRunningsVehicles.Select(async v => await WaitLeaveWorkStationRegion(v.Key)).ToList();

                    var results = await Task.WhenAll(AllWaitLeaveWorkStationTasks);
                    vehicle.NavigationState.ConflicAction = ConflicSolveResult.CONFLIC_ACTION.ACCEPT_GO;

                    return new ConflicSolveResult(results.All(v => v) ? ConflicSolveResult.CONFLIC_ACTION.ACCEPT_GO : ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT);

                    async Task<bool> WaitLeaveWorkStationRegion(IAGV waitingForVehicle)
                    {
                        _isVehicleReadyToEntryWorkStation(waitingForVehicle, out IEnumerable<MapPoint> entryPoints);
                        var _entryPoints = entryPoints.ToList();
                        var workStationTag = waitingForVehicle.CurrentRunningTask().OrderData.To_Station_Tag;
                        while (!_isVehicleLeaveWorkStationRegion(waitingForVehicle, workStationTag, _entryPoints))
                        {
                            await Task.Delay(10);
                            vehicle.NavigationState.ResetNavigationPointsOfPathCalculation();
                            await StaMap.UnRegistPointsOfAGVRegisted(vehicle);
                            if (vehicle.CurrentRunningTask().IsTaskCanceled)
                                throw new TaskCanceledException();

                        }
                        return true;

                    }

                }

                return new ConflicSolveResult(isAllVehicleReadyToWork ? ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT : ConflicSolveResult.CONFLIC_ACTION.ACCEPT_GO);


                bool _isVehicleReadyToEntryWorkStation(IAGV vehicleDoLDULD, out IEnumerable<MapPoint> entryPoints)
                {
                    int workStationTag = vehicleDoLDULD.CurrentRunningTask().OrderData.To_Station_Tag;
                    entryPoints = StaMap.GetPointByTagNumber(workStationTag).TargetNormalPoints();
                    return workStationTag== vehicleDoLDULD.states.Last_Visited_Node || entryPoints.GetTagCollection().Contains(vehicleDoLDULD.states.Last_Visited_Node);
                }

                bool _isVehicleLeaveWorkStationRegion(IAGV vehicleDoLDULD, int workStationTag, List<MapPoint> entryPoints)
                {
                    int currentTag = vehicleDoLDULD.states.Last_Visited_Node;
                    return workStationTag != currentTag && !entryPoints.GetTagCollection().Contains(currentTag);
                }
            }
            return new ConflicSolveResult(ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT);
        }

        private bool IsVehicleReadyToUDULD(IAGV vehicle)
        {
            
            var orderInfo = vehicle.CurrentRunningTask().OrderData;
            if(orderInfo==null)
                return false;
            var currentOrderAction = orderInfo.Action;
            return currentOrderAction != ACTION_TYPE.None && currentOrderAction != ACTION_TYPE.Charge;
        }
    }
}

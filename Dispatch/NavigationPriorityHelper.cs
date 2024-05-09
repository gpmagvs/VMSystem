using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using System.Linq;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;

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
        internal ConflicSolveResult GetPriorityByBecauseDestineConflic(IAGV vehicle, List<IAGV> confliAGVList, MapPoint finalMapPoint)
        {
            Dictionary<IAGV, ACTION_TYPE> conflicAGVCurrentActions = confliAGVList.ToDictionary(agv => agv, agv => agv.CurrentRunningTask().ActionType);
            Dictionary<IAGV, ACTION_TYPE> lduldActionRunningsVehicles = conflicAGVCurrentActions.Where(pair => IsVehicleReadyToUDULD(pair.Key))
                                                                                                .ToDictionary(kp => kp.Key, kp => kp.Value);

            bool isConflicWithUDLUDVehicles = lduldActionRunningsVehicles.Any();
            if (isConflicWithUDLUDVehicles)//與在進行取放貨作業的車輛衝突
            {
                IEnumerable<MapPoint> WorkStations = lduldActionRunningsVehicles.Keys.Select(agv => StaMap.GetPointByTagNumber(agv.CurrentRunningTask().OrderData.To_Station_Tag));
                IEnumerable<MapPoint> entryPointsOfWorkStations = WorkStations.SelectMany(pt => pt.TargetNormalPoints());

                bool isAllVehicleReadyInWorkStations = lduldActionRunningsVehicles.Keys.All(v => _isVehicleInWorkStation(v));

                return new ConflicSolveResult(isAllVehicleReadyInWorkStations ? ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT : ConflicSolveResult.CONFLIC_ACTION.ACCEPT_GO);

                bool _isVehicleInWorkStation(IAGV vehicleDoLDULD)
                {
                    int workStationTag = vehicleDoLDULD.CurrentRunningTask().OrderData.To_Station_Tag;
                    return vehicleDoLDULD.states.Last_Visited_Node == workStationTag;
                }
            }
            return new ConflicSolveResult(ConflicSolveResult.CONFLIC_ACTION.STOP_AND_WAIT);
        }

        private bool IsVehicleReadyToUDULD(IAGV vehicle)
        {
            var orderInfo = vehicle.CurrentRunningTask().OrderData;
            var currentOrderAction = orderInfo.Action;
            return currentOrderAction != ACTION_TYPE.None || currentOrderAction != ACTION_TYPE.Charge;
        }
    }
}

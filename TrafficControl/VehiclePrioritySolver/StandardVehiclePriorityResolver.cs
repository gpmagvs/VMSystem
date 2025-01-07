using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.Extensions;
using VMSystem.TrafficControl.VehiclePrioritySolver.Rules;

namespace VMSystem.TrafficControl.VehiclePrioritySolver
{
    public class StandardVehiclePriorityResolver : BaseVehiclePriorityResolver
    {
        public Dictionary<ACTION_TYPE, int> OrderActionWeightsMap = new Dictionary<ACTION_TYPE, int>
        {
            { ACTION_TYPE.Carry , 1000 },
            { ACTION_TYPE.Unload , 900 },
            { ACTION_TYPE.Load, 800 },
            { ACTION_TYPE.None, 500 },
            { ACTION_TYPE.Charge, 400 },
        };
        private readonly clsTrafficControlParameters _parameters;
        private readonly List<IPriorityRule> _priorityRules;

        public StandardVehiclePriorityResolver() : base()
        {
            _priorityRules = new List<IPriorityRule>{
                new HighestPriorityTaskRule(),
                new WaitingForEnterRegionRule(),
                new ForkLiftParkingRule(),
                new DestinationBlockingRule()
            };
        }

        protected override IEnumerable<IPriorityRule> GetPriorityResolvers()
        {
            return _priorityRules;
        }

        protected override PrioritySolverResult? ResolveByWeight(IEnumerable<IAGV> DeadLockVehicles)
        {
            // 實作原本的權重計算邏輯...
            Dictionary<IAGV, int> orderedByWeight = DeadLockVehicles.ToDictionary(v => v, v => CalculateWeights(v));
            IEnumerable<IAGV> ordered = new List<IAGV>();
            if (orderedByWeight.First().Value == orderedByWeight.Last().Value)
            {
                //權重相同,先等待者為高優先權車輛
                ordered = DeadLockVehicles.OrderBy(vehicle => (DateTime.Now - vehicle.NavigationState.StartWaitConflicSolveTime).TotalSeconds);
            }
            else
                ordered = orderedByWeight.OrderBy(kp => kp.Value).Select(kp => kp.Key);
            return new PrioritySolverResult
            {
                lowPriorityVehicle = ordered.First(),
                highPriorityVehicle = ordered.Last(),
                IsAvoidUseParkablePort = false
            };
        }

        public int CalculateWeights(IAGV vehicle)
        {
            var currentOrderHandler = vehicle.CurrentOrderHandler();
            var runningTask = currentOrderHandler.RunningTask;
            var runningStage = runningTask.Stage;
            var orderInfo = currentOrderHandler.OrderData;
            var orderAction = orderInfo.Action;

            int weights = OrderActionWeightsMap[orderAction];

            if (orderAction == ACTION_TYPE.Carry)
            {
                if (runningStage == VehicleMovementStage.Traveling_To_Source)
                    weights += 50;
                else
                    weights += 40;
            }

            if (orderAction == ACTION_TYPE.Carry || orderAction == ACTION_TYPE.Load || orderAction == ACTION_TYPE.Unload)
            {
                if (orderAction != ACTION_TYPE.Carry)
                {
                    int workStationTag = 0;
                    workStationTag = orderInfo.To_Station_Tag;
                    MapPoint workStationPt = StaMap.GetPointByTagNumber(workStationTag);
                    weights = weights * workStationPt.PriorityOfTask;
                }
                else
                {
                    MapPoint sourcePt = StaMap.GetPointByTagNumber(orderInfo.From_Station_Tag);
                    MapPoint destinePt = StaMap.GetPointByTagNumber(orderInfo.To_Station_Tag);
                    weights = weights * sourcePt.PriorityOfTask * destinePt.PriorityOfTask;

                }
            }

            return weights;
        }
    }
}

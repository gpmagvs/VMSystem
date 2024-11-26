using VMSystem.AGV;
using VMSystem.Extensions;

namespace VMSystem.TrafficControl.VehiclePrioritySolver.Rules
{
    public class WaitingForEnterRegionRule : IPriorityRule
    {
        public PrioritySolverResult? ResolvePriority(IEnumerable<IAGV> deadlockedVehicles)
        {
            IAGV waitingForEntryRegionVehicle = deadlockedVehicles.FirstOrDefault(v => v.NavigationState.RegionControlState.IsWaitingForEntryRegion);
            if (waitingForEntryRegionVehicle == null)
                return null;

            var lowPriorityVehicle = waitingForEntryRegionVehicle;
            var highPriorityVehicle = deadlockedVehicles.First(agv => agv != waitingForEntryRegionVehicle);

            ForkLiftParkingRule forkLiftParkingRule = new ForkLiftParkingRule();
            PrioritySolverResult? parkToRackPrioty = forkLiftParkingRule.ResolvePriority(new List<IAGV>() { lowPriorityVehicle, highPriorityVehicle });

            return new PrioritySolverResult()
            {
                lowPriorityVehicle = lowPriorityVehicle,
                highPriorityVehicle = highPriorityVehicle,
                IsAvoidUseParkablePort = parkToRackPrioty == null ? false : parkToRackPrioty.IsAvoidUseParkablePort && !lowPriorityVehicle.IsAGVHasCargoOrHasCargoID(),
                IsWaitingEnterRegionVehicleShouldYield = true
            };
        }
    }
}

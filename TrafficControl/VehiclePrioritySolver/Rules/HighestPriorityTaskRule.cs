using VMSystem.AGV;
using VMSystem.Extensions;

namespace VMSystem.TrafficControl.VehiclePrioritySolver.Rules
{
    public class HighestPriorityTaskRule : IPriorityRule
    {
        public PrioritySolverResult? ResolvePriority(IEnumerable<IAGV> deadlockedVehicles)
        {
            var superOrderVehicle = deadlockedVehicles.FirstOrDefault(v => v.CurrentRunningTask().OrderData.IsHighestPriorityTask);
            if (superOrderVehicle == null)
                return null;

            var lowPriorityVehicle = deadlockedVehicles.First(agv => agv != superOrderVehicle);
            return new PrioritySolverResult()
            {
                lowPriorityVehicle = lowPriorityVehicle,
                highPriorityVehicle = superOrderVehicle,
                IsAvoidUseParkablePort = false
            };
        }
    }
}

using VMSystem.AGV;

namespace VMSystem.TrafficControl.VehiclePrioritySolver.Rules
{
    public interface IPriorityRule
    {
        PrioritySolverResult? ResolvePriority(IEnumerable<IAGV> deadlockedVehicles);
    }
}

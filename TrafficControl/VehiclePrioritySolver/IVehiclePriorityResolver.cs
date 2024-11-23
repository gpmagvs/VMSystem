using VMSystem.AGV;

namespace VMSystem.TrafficControl.VehiclePrioritySolver
{
    public interface IVehiclePriorityResolver
    {
        PrioritySolverResult ResolvePriority(IEnumerable<IAGV> deadlockedVehicles);
    }
}

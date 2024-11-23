using VMSystem.AGV;

namespace VMSystem.TrafficControl.VehiclePrioritySolver
{
    public class PrioritySolverResult
    {
        public IAGV highPriorityVehicle {  get; set; }
        public IAGV lowPriorityVehicle {  get; set; }
        public bool IsAvoidUseParkablePort { get; set; }=false;
    }
}

using VMSystem.AGV;
using VMSystem.TrafficControl.VehiclePrioritySolver.Rules;

namespace VMSystem.TrafficControl.VehiclePrioritySolver
{
    public abstract class BaseVehiclePriorityResolver : IVehiclePriorityResolver
    {
        protected BaseVehiclePriorityResolver()
        {
        }

        public virtual PrioritySolverResult ResolvePriority(IEnumerable<IAGV> deadlockedVehicles)
        {
            // 依序檢查各種優先權規則
            foreach (var resolver in GetPriorityResolvers())
            {
                var result = resolver.ResolvePriority(deadlockedVehicles);
                if (result!=null)
                    return result;
            }

            // 如果沒有特殊規則適用，使用權重計算
            return ResolveByWeight(deadlockedVehicles);
        }

        protected abstract IEnumerable<IPriorityRule> GetPriorityResolvers();
        protected abstract PrioritySolverResult ResolveByWeight(IEnumerable<IAGV> vehicles);
    }
}

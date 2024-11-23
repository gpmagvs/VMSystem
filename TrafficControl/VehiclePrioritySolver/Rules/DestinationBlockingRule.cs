using AGVSystemCommonNet6.Notify;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;

namespace VMSystem.TrafficControl.VehiclePrioritySolver.Rules
{
    public class DestinationBlockingRule : IPriorityRule
    {

        public DestinationBlockingRule()
        {
        }

        public PrioritySolverResult? ResolvePriority(IEnumerable<IAGV> deadlockedVehicles)
        {
            // 實作原本的終點阻擋檢查邏輯...
            if (deadlockedVehicles.FirstOrDefault(agv => _IsAnyAGVInNearbyOfDestineOfOthers(agv)) == null)
                return null;
            var _lowPAGV = deadlockedVehicles.First(agv => _IsAnyAGVInNearbyOfDestineOfOthers(agv));
            var _highPAGV = deadlockedVehicles.First(agv => agv != _lowPAGV);
            NotifyServiceHelper.INFO($"{_lowPAGV.Name} 位於其他車輛任務終點或鄰近點.應優先避讓");
            return new PrioritySolverResult
            {
                lowPriorityVehicle = _lowPAGV,
                highPriorityVehicle = _highPAGV,
                IsAvoidUseParkablePort = false
            };

            bool _IsAnyAGVInNearbyOfDestineOfOthers(IAGV _agv)
            {
                var otherAgvList = deadlockedVehicles.Where(agv => agv != _agv);
                var otherAGVDestineMapPoints = otherAgvList.Select(agv => StaMap.GetPointByTagNumber(agv.CurrentRunningTask().DestineTag)).ToList();
                bool _isAGVAtSomeoneDestine = otherAGVDestineMapPoints.Any(pt => pt.TagNumber == _agv.currentMapPoint.TagNumber);
                if (_isAGVAtSomeoneDestine)
                    return true;
                var allNearbyPtOfDestines = otherAGVDestineMapPoints.SelectMany(destinePt => destinePt.TargetNormalPoints());
                bool _isAGVAtSomeoneNearPointOfDestine = allNearbyPtOfDestines.Any(pt => pt.TagNumber == _agv.currentMapPoint.TagNumber);
                return _isAGVAtSomeoneNearPointOfDestine;
            }
        }

    }
}

using AGVSystemCommonNet6.Notify;
using AGVSystemCommonNet6;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.TrafficControl.VehiclePrioritySolver.Rules
{
    public class ForkLiftParkingRule : IPriorityRule
    {

        public ForkLiftParkingRule()
        {
        }

        public PrioritySolverResult? ResolvePriority(IEnumerable<IAGV> deadlockedVehicles)
        {
            if (!TrafficControlCenter.TrafficControlParameters.Experimental.UseRackToAvoid)
                return null;
            // 實作原本的叉車避讓邏輯...
            if (!deadlockedVehicles.Any(vehicle => vehicle.model == clsEnums.AGV_TYPE.FORK && !vehicle.NavigationState.AvoidActionState.IsParkToWIPButNoPathToGo))
                return null;
            //僅只抓出沒載貨的車輛
            List<IAGV> _vehicles = deadlockedVehicles.Where(agv => agv.states.Cargo_Status == 0 && (!agv.states.CSTID.Any() || string.IsNullOrEmpty(agv.states.CSTID.First()))).ToList();
            //排序:嘗試把在可停車點之相鄰點的車排在前面
            _vehicles = _vehicles.OrderByDescending(vehicle => vehicle.currentMapPoint.TargetParkableStationPoints().Count())
                                 .ToList();

            IAGV forkAGV = _vehicles.FirstOrDefault(vehicle => vehicle.model == clsEnums.AGV_TYPE.FORK);
            if (forkAGV == null || !GetParkablePointOfAGVInRegion(forkAGV).Any())
                return null;

            IAGV HighPriortyAGV = deadlockedVehicles.First(v => v != forkAGV);
            NotifyServiceHelper.INFO($"叉車-{forkAGV.Name}應優先避讓至WIP PORT");
            return new PrioritySolverResult
            {
                highPriorityVehicle = forkAGV,
                lowPriorityVehicle = HighPriortyAGV,
                IsAvoidUseParkablePort = true,
            };

        }
        IEnumerable<MapPoint> GetParkablePointOfAGVInRegion(IAGV forkAGV)
        {
            var currentRegion = forkAGV.currentMapPoint.GetRegion();
            if (currentRegion == null || currentRegion.RegionType == MapRegion.MAP_REGION_TYPE.UNKNOWN)
                return new MapPoint[0];

            IEnumerable<MapPoint> parkablePortPointsInRegion = currentRegion.GetParkablePointOfRegion(forkAGV);
            if (!parkablePortPointsInRegion.Any())
                return new List<MapPoint>();
            return parkablePortPointsInRegion;
        }
    }
}

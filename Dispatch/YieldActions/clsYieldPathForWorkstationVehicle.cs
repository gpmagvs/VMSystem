using VMSystem.AGV;
using VMSystem.Dispatch.YieldActions;

namespace VMSystem.Dispatch.YieldActions
{
    public class clsYieldPathForWorkstationVehicle : clsLowPriorityVehicleMove
    {
        public clsYieldPathForWorkstationVehicle(IAGV _Vehicle, IAGV HightPriorityVehicle) : base(_Vehicle, HightPriorityVehicle)
        {
        }
    }

}

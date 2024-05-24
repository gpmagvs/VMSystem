using AGVSystemCommonNet6.Notify;
using VMSystem.AGV;

namespace VMSystem.Dispatch.YieldActions
{
    /// <summary>
    /// 解決在EQ內互相等待
    /// </summary>
    public class clsLowPriorityVehicleWaitAtWorkStation : clsLowPriorityVehicleMove
    {
        public clsLowPriorityVehicleWaitAtWorkStation(IAGV _Vehicle, IAGV HightPriorityVehicle) : base(_Vehicle, HightPriorityVehicle)
        {
        }

        public override async Task<IAGV> StartSolve()
        {
            await Task.Delay(1000);
            if (!_LowProrityVehicle.NavigationState.IsWaitingForLeaveWorkStation)
                return _LowProrityVehicle;

            _HightPriorityVehicle.NavigationState.LeaveWorkStationHighPriority = true;
            _HightPriorityVehicle.NavigationState.IsWaitingForLeaveWorkStation = false;
            NotifyServiceHelper.SUCCESS($"{_HightPriorityVehicle.Name}(優先) 與 {_LowProrityVehicle.Name} 車輛在設備內相互等待衝突已解決!");
            return _HightPriorityVehicle;
        }
    }
}

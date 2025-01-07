using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class ParkOrderHandler : ChargeOrderHandler
    {
        public ParkOrderHandler() : base() { }
        public ParkOrderHandler(SemaphoreSlim taskTbModifyLock) : base(taskTbModifyLock)
        {
        }

        public override ACTION_TYPE OrderAction => ACTION_TYPE.Park;
    }

}

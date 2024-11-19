using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers
{
    public class ChargeStationChanger : DestineChangeBase
    {
        public ChargeStationChanger(IAGV agv, clsTaskDto order, AGVSDbContext db, SemaphoreSlim taskTableLocker) : base(agv, order, db, taskTableLocker)
        {
        }

        internal override bool IsNeedChange()
        {
            throw new NotImplementedException();
        }

    }
}

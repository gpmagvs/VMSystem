using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers
{
    /// <summary>
    /// 這是一個用來變換目的地的類別
    /// </summary>
    public abstract class DestineChangeBase : VehicleOrderController
    {
        readonly IAGV agv;
        readonly clsTaskDto order;

        /// <summary>
        /// 目標站點Tag
        /// </summary>
        public int destineTag => order.To_Station_Tag;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="agv">車輛</param>
        /// <param name="order">任務訂單</param>
        public DestineChangeBase(IAGV agv, clsTaskDto order, AGVSDbContext db, SemaphoreSlim taskTableLocker) : base(db, taskTableLocker)
        {
            this.agv = agv;
            this.order = order;
        }

        internal async Task StartMonitorAsync()
        {
            await Task.Delay(1).ContinueWith(async t =>
            {
                while (!IsCurrentSubTaskFinish())
                {
                    await Task.Delay(100);
                    if (IsNeedChange())
                    {
                        await CancelOrderAndWaitVehicleIdle(this.agv, this.order);

                        await ModifyOrder(order);
                    }

                }
            });
        }

        internal abstract bool IsNeedChange();

        protected virtual bool IsCurrentSubTaskFinish()
        {
            if (agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN)
                return true;

            if (agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                return true;
            TaskBase? currentRunTask = agv.CurrentRunningTask();
            if (currentRunTask == null)
                return true;

            if (currentRunTask.Stage != VehicleMovementStage.Traveling_To_Destine)
                return true;

            return false;
        }

    }
}

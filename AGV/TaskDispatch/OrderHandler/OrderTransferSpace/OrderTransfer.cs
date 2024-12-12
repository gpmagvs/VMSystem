using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using NLog;
using System.Collections.Concurrent;
using System.Threading;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace
{
    public abstract class OrderTransfer : VehicleOrderController
    {
        public enum STATES
        {
            ABORTED,
            BETTER_VEHICLE_SEARCHING,
            ORDER_TRANSFERING,
            ORDER_TRANSFERIED,

        }
        protected readonly IAGV orderOwner;
        protected readonly clsTaskDto order;
        protected readonly OrderTransferConfiguration configuration = new OrderTransferConfiguration();
        protected CancellationTokenSource cancellationTokenSource;


        static Logger logger = LogManager.GetCurrentClassLogger();

        private static ConcurrentDictionary<string, int> OrderTransferTimesStore = new ConcurrentDictionary<string, int>();
        private STATES _State = STATES.ABORTED;
        public STATES State
        {
            get => _State;
            protected set
            {
                if (_State != value)
                {
                    _State = value;
                    Log($"State Changed to {value}");
                }
            }
        }
        public OrderTransfer(IAGV orderOwner, clsTaskDto order, OrderTransferConfiguration configuration, SemaphoreSlim taskTableLocker) : base(taskTableLocker)
        {
            this.orderOwner = orderOwner;
            this.order = order;
            this.configuration = configuration;
        }
        public void Abort()
        {
            cancellationTokenSource.Cancel();
        }

        internal bool OrderDone()
        {
            bool removed = OrderTransferTimesStore.TryRemove(order.TaskName, out int count);
            Log($"Order finish invoked. Total transfer count = {count}");
            return removed;
        }

        internal async Task ReStartAsync(string restartReason = "")
        {
            Log($"Restart Order Transfer Process. Reason input:{restartReason}");
            OrderTransferTimesStore.Remove(order.TaskName, out _);
            await WatchStart();
        }

        internal async Task WatchStart()
        {
            Log("Start Watching");
            cancellationTokenSource = new CancellationTokenSource();
            bool _isOrderTransferStateStored = OrderTransferTimesStore.TryGetValue(order.TaskName, out int times);
            if (_isOrderTransferStateStored && times >= configuration.MaxTransferTimes)
            {
                Log($"Max transfer time reach limit({configuration.MaxTransferTimes})");
                State = STATES.ORDER_TRANSFERIED;
                return;
            }

            if (!_isOrderTransferStateStored)
                OrderTransferTimesStore.TryAdd(order.TaskName, 0);

            await Task.Delay(1).ContinueWith(async (t) =>
            {
                try
                {
                    bool transferDone = false;
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        State = STATES.BETTER_VEHICLE_SEARCHING;

                        (bool found, IAGV betterVehicle) = await TryFindBetterVehicle();

                        if (found)
                        {
                            Log($"Try transfer order to {betterVehicle.Name}");
                            Log($"Cancel Task Of Order Owner");
                            Log($"Wait original order owner state changed to IDLE...");
                            (bool confirmed, string message) = await CancelOrderAndWaitVehicleIdle(orderOwner, order, "Change Vehicle To Execute");
                            if (!confirmed)
                            {
                                Log($"Wait original order owner state changed to IDLE...TIMEOUT");
                                continue;
                            }
                            State = STATES.ORDER_TRANSFERING;

                            Log($"Wait order not RUN...");
                            await WaitOrderNotRun(order);

                            Log($"Start transfer order to better vehicle-{betterVehicle.Name}");
                            if (await TryTransferOrderToAnotherVehicle(betterVehicle))
                            {
                                Log($"Transfer order to {betterVehicle.Name} successfully");
                                transferDone = true;
                                State = STATES.ORDER_TRANSFERIED;
                                OrderTransferTimesStore[order.TaskName] = OrderTransferTimesStore[order.TaskName] + 1;
                                break;
                            }
                            else
                            {
                                Log($"Transfer order to {betterVehicle.Name} FAIL!!!!!!!!!!!!!!");
                            }
                        }
                        await Task.Delay(1000);
                    }
                }
                catch (TaskCanceledException ex)
                {
                    Log(ex.Message);
                }
                finally
                {
                    State = STATES.ABORTED;
                    if (cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        Log($"Monitor process is Aborted");
                    }
                }

            });


        }

        /// <summary>
        /// 是否需要進行訂單轉移
        /// </summary>
        /// <returns></returns>
        public abstract Task<(bool found, IAGV betterVehicle)> TryFindBetterVehicle();

        internal async Task<bool> TryTransferOrderToAnotherVehicle(IAGV betterVehicle)
        {
            // Transfer order to other vehicle
            if (betterVehicle == null)
                return false;

            //Second, Transfer the order to the target vehicle
            bool orderModifiedSuccess = await ModifyOrder(betterVehicle);

            return orderModifiedSuccess;
        }

        private async Task<bool> ModifyOrder(IAGV betterVehicle)
        {
            try
            {
                clsTaskDto orderDto = agvsDb.Tasks.AsNoTracking().FirstOrDefault(t => t.TaskName == order.TaskName);
                if (orderDto == null)
                    return false;
                orderDto.FinishTime = DateTime.MinValue;
                orderDto.State = AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.WAIT;
                orderDto.FailureReason = "";
                orderDto.DesignatedAGVName = betterVehicle.Name;
                orderDto.isVehicleAssignedChanged = true;
                return await base.ModifyOrder(orderDto);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            finally
            {
            }
        }

        internal void Log(string message)
        {
            string _logPrefix = $"[{order.TaskName}-Order Owner:{orderOwner.Name}]";
            logger.Trace($"{_logPrefix} {message}");
        }

    }
}

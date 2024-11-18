using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using NLog;
using System.Collections.Concurrent;
using System.Threading;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace
{
    public abstract class OrderTransfer
    {
        public enum STATES
        {
            BETTER_VEHICLE_SEARCHING,
            ORDER_TRANSFERING,
            ORDER_TRANSFERIED,

        }
        protected readonly IAGV orderOwner;
        protected readonly clsTaskDto order;
        protected readonly OrderTransferConfiguration configuration = new OrderTransferConfiguration();
        protected CancellationTokenSource cancellationTokenSource;

        internal AGVSDbContext agvsDb;
        internal SemaphoreSlim tasksTableDbLock;

        static Logger logger = LogManager.GetCurrentClassLogger();

        private static ConcurrentDictionary<string, int> OrderTransferTimesStore = new ConcurrentDictionary<string, int>();
        private STATES _State = STATES.BETTER_VEHICLE_SEARCHING;
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
        public OrderTransfer(IAGV orderOwner, clsTaskDto order, OrderTransferConfiguration configuration)
        {
            this.orderOwner = orderOwner;
            this.order = order;
            this.configuration = configuration;
        }
        public void Abort()
        {
            cancellationTokenSource.Cancel();
        }

        internal void OrderDone()
        {
            OrderTransferTimesStore.TryRemove(order.TaskName, out int count);
            Log($"Order finish invoked. Total transfer count = {count}");
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
                        if (TryFindBetterVehicle(out IAGV betterVehicle))
                        {

                            Log($"Try transfer order to {betterVehicle.Name}");
                            Log($"Cancel Task Of Order Owner");
                            await orderOwner.CancelTaskAsync(order.TaskName, "Change Vehicle To Execute");
                            Log($"Wait original order owner state changed to IDLE...");
                            (bool confirmed, string message) = await WaitOwnerVehicleIdle();

                            if (!confirmed)
                            {
                                Log($"Wait original order owner state changed to IDLE...TIMEOUT");
                                continue;
                            }
                            State = STATES.ORDER_TRANSFERING;

                            Log($"Wait order not RUN...");
                            await WaitOrderNotRun();

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
                finally
                {
                }

            });


        }

        private async Task WaitOrderNotRun()
        {
            while (IsOrderRunning())
            {
                await Task.Delay(1000);
            }
        }

        /// <summary>
        /// 是否需要進行訂單轉移
        /// </summary>
        /// <returns></returns>
        public abstract bool TryFindBetterVehicle(out IAGV betterVehicle);

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
                await this.tasksTableDbLock.WaitAsync();
                if (agvsDb == null)
                    return false;
                clsTaskDto orderDto = agvsDb.Tasks.FirstOrDefault(t => t.TaskName == order.TaskName);
                if (orderDto == null)
                    return false;
                orderDto.FinishTime = DateTime.MinValue;
                orderDto.State = AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.WAIT;
                orderDto.FailureReason = "";
                orderDto.DesignatedAGVName = betterVehicle.Name;
                agvsDb.SaveChanges();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            finally
            {
                this.tasksTableDbLock.Release();
            }
        }

        private async Task<(bool, string)> WaitOwnerVehicleIdle()
        {
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (orderOwner.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN ||
                orderOwner.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
            {
                try
                {
                    await Task.Delay(10, cancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    //Timeout
                    return (false, "Wait AGV IDLE Timeout");
                }
            }
            return (true, "");
        }


        bool IsOrderRunning()
        {
            return DatabaseCaches.TaskCaches.InCompletedTasks.Any(tk => tk.TaskName == order.TaskName);
        }

        internal void Log(string message)
        {
            string _logPrefix = $"[{order.TaskName}-Order Owner:{orderOwner.Name}]";
            logger.Trace($"{_logPrefix} {message}");
        }

    }
}

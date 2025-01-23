using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using Microsoft.EntityFrameworkCore;
using NLog;
using System.Collections.Concurrent;
using System.Threading;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;

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
            cancellationTokenSource?.Cancel();
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
        public override async Task<(bool confirm, string message)> CancelOrderAndWaitVehicleIdle(IAGV agv, clsTaskDto order, string reason, int timeout = 300)
        {
            await agv.CancelTaskAsync(order.TaskName, false, reason);
            await SetOrderIsChangeVehicleState();
            return await WaitOwnerVehicleIdle(agv, timeout);
        }
        internal async Task WatchStart()
        {
            if (this.order.isSpeficVehicleAssigned)
                return;
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
                            await LogOriginalOwnerCurrentState();
                            Log($"Try transfer order to {betterVehicle.Name}");
                            Log($"Cancel Task Of Order Owner");
                            Log($"Wait original order owner state changed to IDLE...");
                            (bool confirmed, string message) = await CancelOrderAndWaitVehicleIdle(orderOwner, order, "Change Vehicle To Execute", 300);
                            if (!confirmed)
                            {
                                Log($"Wait original order owner state changed to IDLE...TIMEOUT({message})");
                                await Task.Delay(1000);
                            }
                            await LogOriginalOwnerCurrentState();
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

        private async Task LogOriginalOwnerCurrentState()
        {
            try
            {
                MapPoint currentPoint = orderOwner.currentMapPoint;
                bool isWaittingTrafficControlSolved = orderOwner.NavigationState.IsWaitingConflicSolve;
                VehicleMovementStage currentStage = VehicleMovementStage.Not_Start_Yet;
                VehicleMovementStage currentSubStage = VehicleMovementStage.Not_Start_Yet;
                bool isCurrentTaskCancelling = false;
                TaskBase? currentTask = orderOwner.CurrentRunningTask();

                if (currentTask != null)
                {
                    currentStage = currentTask.Stage;
                    currentSubStage = currentTask.subStage;
                }

                Log($"Original Order Owner AGV Current State: Main Status={orderOwner.main_state}/" +
                    $"Current Point={currentPoint.Graph.Display}(Tag:{currentPoint.TagNumber})/" +
                    $"等待交管狀態:{(isWaittingTrafficControlSolved ? "YES" : "NO")}/" +
                    $"當前任務是否已取消:{(isCurrentTaskCancelling ? "YES" : "NO")}/" +
                    $"當前任務進度:{currentStage}/" +
                    $"當前任務子狀態:{currentSubStage}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Exception occur when LogOriginalOwnerCurrentState:{ex.Message}");
            }
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
            await Task.Delay(1000);
            //Second, Transfer the order to the target vehicle
            bool orderModifiedSuccess = await ModifyOrder(betterVehicle);

            return orderModifiedSuccess;
        }
        private async Task<bool> SetOrderIsChangeVehicleState()
        {
            try
            {
                this.order.isVehicleAssignedChanged = true;
                return await base.ModifyOrder(this.order);
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
        private async Task<bool> ModifyOrder(IAGV betterVehicle)
        {
            try
            {
                using AGVSDatabase agvsDb = new AGVSDatabase();
                clsTaskDto orderDto = agvsDb.tables.Tasks.AsNoTracking().FirstOrDefault(t => t.TaskName == order.TaskName);
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

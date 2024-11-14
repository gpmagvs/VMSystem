using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using System.Threading;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace
{
    public abstract class OrderTransfer
    {
        protected readonly IAGV orderOwner;
        protected readonly clsTaskDto order;
        protected CancellationTokenSource cancellationTokenSource;

        internal AGVSDbContext agvsDb;
        internal SemaphoreSlim tasksTableDbLock;
        public OrderTransfer(IAGV orderOwner, clsTaskDto order)
        {
            this.orderOwner = orderOwner;
            this.order = order;
        }
        public void Abort()
        {
            cancellationTokenSource.Cancel();
        }

        internal async Task WatchStart()
        {
            cancellationTokenSource = new CancellationTokenSource();
            await Task.Delay(1).ContinueWith(async (t) =>
            {

                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (TryFindBetterVehicle(out IAGV betterVehicle))
                    {
                        await orderOwner.CancelTaskAsync(order.TaskName, "Change Vehicle To Execute");
                        (bool confirmed, string message) = await WaitOwnerVehicleIdle();

                        if (!confirmed)
                            continue;

                        //double check
                        if (!TryFindBetterVehicle(out betterVehicle))
                            break;


                        await WaitOrderNotRun();

                        if (await TryTransferOrderToAnotherVehicle(betterVehicle))
                        {
                            Console.WriteLine("Transfer order to another vehicle successfully");
                            break;
                        }
                        else
                        {
                            Console.WriteLine("Failed to transfer order to another vehicle");
                        }
                    }
                    await Task.Delay(1000);
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
    }
}

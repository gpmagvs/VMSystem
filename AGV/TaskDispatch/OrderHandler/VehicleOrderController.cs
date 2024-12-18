using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment;
using AutoMapper;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class VehicleOrderController
    {

        public AGVSDbContext agvsDb;
        public readonly SemaphoreSlim tasksTableDbLock;
        public VehicleOrderController(SemaphoreSlim taskTableLocker)
        {
            tasksTableDbLock = taskTableLocker;
            agvsDb = new AGVSDatabase().tables;
        }

        public virtual async Task<(bool confirm, string message)> CancelOrderAndWaitVehicleIdle(IAGV agv, clsTaskDto order, string reason, int timeout = 300)
        {
            await agv.CancelTaskAsync(order.TaskName, reason);
            return await WaitOwnerVehicleIdle(agv, timeout);
        }

        public async Task<bool> AddNewOrder(clsTaskDto newOrder)
        {
            try
            {
                await this.tasksTableDbLock.WaitAsync();
                if (agvsDb == null)
                    return false;
                agvsDb.Tasks.Add(newOrder);
                await agvsDb.SaveChangesAsync();
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
        public async Task<bool> ModifyOrder(clsTaskDto orderModified)
        {
            try
            {
                await this.tasksTableDbLock.WaitAsync();
                if (agvsDb == null)
                    return false;

                MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.CreateMap<clsTaskDto, clsTaskDto>());
                IMapper mapper = mapperConfig.CreateMapper();

                clsTaskDto orderDto = agvsDb.Tasks.FirstOrDefault(t => t.TaskName == orderModified.TaskName);
                if (orderDto == null)
                    return false;
                mapper.Map(orderModified, orderDto);
                agvsDb.Entry(orderDto).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
                await agvsDb.SaveChangesAsync();
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
        public async Task WaitOrderNotRun(clsTaskDto order)
        {
            while (IsOrderRunning(order))
            {
                await Task.Delay(1000);
            }
        }

        protected async Task<(bool, string)> WaitOwnerVehicleIdle(IAGV agv, int timeout = 300)
        {
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            while (agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN ||
                agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
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
        private bool IsOrderRunning(clsTaskDto order)
        {
            return DatabaseCaches.TaskCaches.InCompletedTasks.Any(tk => tk.TaskName == order.TaskName);
        }
    }
}

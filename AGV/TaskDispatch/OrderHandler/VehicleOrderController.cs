using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment;
using AutoMapper;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class VehicleOrderController
    {

        public readonly AGVSDbContext agvsDb;
        public readonly SemaphoreSlim tasksTableDbLock;
        public VehicleOrderController(AGVSDbContext db, SemaphoreSlim taskTableLocker)
        {
            agvsDb = db;
            tasksTableDbLock = taskTableLocker;
        }

        public async Task<(bool confirm, string message)> CancelOrderAndWaitVehicleIdle(IAGV agv, clsTaskDto order)
        {
            await agv.CancelTaskAsync(order.TaskName, "Change Vehicle To Execute");
            return await WaitOwnerVehicleIdle(agv);
        }
        public async Task WaitOrderNotRun(clsTaskDto order)
        {
            while (IsOrderRunning(order))
            {
                await Task.Delay(1000);
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
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            finally
            {
                await agvsDb.SaveChangesAsync();
                this.tasksTableDbLock.Release();
            }
        }

        private async Task<(bool, string)> WaitOwnerVehicleIdle(IAGV agv)
        {
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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

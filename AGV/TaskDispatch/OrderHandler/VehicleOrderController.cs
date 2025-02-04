using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment;
using AGVSystemCommonNet6.Microservices.AGVS;
using AutoMapper;
using VMSystem.Extensions;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class VehicleOrderController
    {

        public readonly SemaphoreSlim tasksTableDbLock;

        public bool IsCycleStopRaised { get; protected set; }
        protected ManualResetEvent CancelTaskMRE = new ManualResetEvent(false);
        protected ManualResetEvent CycleStopProgressRunMRE = new ManualResetEvent(false);
        protected bool _RestartFlag = false;


        public VehicleOrderController(SemaphoreSlim taskTableLocker)
        {
            tasksTableDbLock = taskTableLocker;
        }
        internal bool WaitCycleStopProgressRun()
        {
            return CycleStopProgressRunMRE.WaitOne(TimeSpan.FromSeconds(5));
        }
        internal void ReadyToCycleStop()
        {
            CancelTaskMRE.Set();
        }
        public virtual async Task<(bool confirm, string message)> CancelOrderAndWaitVehicleIdle(IAGV agv, clsTaskDto order, string reason, int timeout = 300)
        {
            await agv.CancelTaskAsync(order.TaskName, false, reason);
            return await WaitOwnerVehicleIdle(agv, timeout);
        }

        public async Task<bool> AddNewOrder(clsTaskDto newOrder)
        {
            try
            {
                await this.tasksTableDbLock.WaitAsync();

                try
                {
                    await AddOrderWithWebAPI(newOrder);
                }
                catch (Exception)
                {
                    await AddOrderWithDataBaseAccessDirectly(newOrder);
                }
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
                try
                {
                    await ModifyOrderWithWebAPI(orderModified);
                }
                catch (Exception)
                {
                    await ModifyOrderWithDataBaseAccessDirectly(orderModified);
                }
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

        private async Task AddOrderWithWebAPI(clsTaskDto order)
        {
            await AGVSSerivces.DATABASE.AddTaskDto(order);
        }
        private async Task AddOrderWithDataBaseAccessDirectly(clsTaskDto order)
        {
            using AGVSDatabase agvsDb = new AGVSDatabase();
            agvsDb.tables.Tasks.Add(order);
            await agvsDb.SaveChanges();
        }

        private async Task ModifyOrderWithWebAPI(clsTaskDto order)
        {
            await AGVSSerivces.DATABASE.ModifyTaskDto(order);
        }
        private async Task ModifyOrderWithDataBaseAccessDirectly(clsTaskDto order)
        {
            using AGVSDatabase agvsDb = new AGVSDatabase();
            MapperConfiguration mapperConfig = new MapperConfiguration(cfg => cfg.CreateMap<clsTaskDto, clsTaskDto>());
            IMapper mapper = mapperConfig.CreateMapper();
            clsTaskDto orderDto = agvsDb.tables.Tasks.FirstOrDefault(t => t.TaskName == order.TaskName);
            if (orderDto == null)
                return;
            mapper.Map(order, orderDto);
            agvsDb.tables.Entry(orderDto).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
            await agvsDb.SaveChanges();
        }


        protected async Task<(bool, string)> WaitOwnerVehicleIdle(IAGV agv, int timeout = 300)
        {
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            while (IsVehicleNotIDLE(agv))
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

        protected bool IsVehicleNotIDLE(IAGV agv)
        {
            return agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN;
        }

        private bool IsOrderRunning(clsTaskDto order)
        {
            return DatabaseCaches.TaskCaches.InCompletedTasks.Any(tk => tk.TaskName == order.TaskName);
        }
    }
}

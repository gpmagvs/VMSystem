using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AutoMapper;
using Nini.Config;
using NLog;

namespace VMSystem
{
    public abstract class clsTaskDatabaseWriteableAbstract : IDisposable
    {
        public readonly AGVSDatabase agvsDb;
        private SemaphoreSlim taskTbModifyLock;
        private Logger logger = LogManager.GetLogger("TaskOrderDatabaseModifier");
        static IMapper mapper;
        protected bool disposedValue;

        static clsTaskDatabaseWriteableAbstract()
        {
            MapperConfiguration config = new(cfg => cfg.CreateMap<clsTaskDto, clsTaskDto>());
            mapper = config.CreateMapper();
        }
        public clsTaskDatabaseWriteableAbstract()
        {
            this.agvsDb = new AGVSDatabase();
        }
        public clsTaskDatabaseWriteableAbstract(SemaphoreSlim taskTbModifyLock)
        {
            this.agvsDb = new AGVSDatabase();
            this.taskTbModifyLock = taskTbModifyLock;
        }

        public async Task ModifyOrder(clsTaskDto dto)
        {
            try
            {
                await taskTbModifyLock.WaitAsync();
                if (dto == null)
                    return;


                var entity = agvsDb.tables.Tasks.FirstOrDefault(tk => tk.TaskName == dto.TaskName);
                if (entity != null)
                {
                    while (agvsDb.tables.IsTaskTableLocking())
                    {
                        await Task.Delay(100);
                    }
                    mapper.Map(dto, entity);
                    int save_cnt = await agvsDb.SaveChanges();
                    logger.Trace($"Task Order-[{dto.TaskName}](Assigned For={entity.DesignatedAGVName},State={entity.StateName}) content changed \r\n{dto.ToJson()}");
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
            finally
            {
                taskTbModifyLock.Release();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 處置受控狀態 (受控物件)
                }

                // TODO: 釋出非受控資源 (非受控物件) 並覆寫完成項
                // TODO: 將大型欄位設為 Null
                //agvsDb?.Dispose();
                disposedValue = true;
            }
        }

        // // TODO: 僅有當 'Dispose(bool disposing)' 具有會釋出非受控資源的程式碼時，才覆寫完成項
        // ~clsTaskDatabaseWriteableAbstract()
        // {
        //     // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

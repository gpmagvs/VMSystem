using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AutoMapper;
using NLog;

namespace VMSystem
{
    public abstract class clsTaskDatabaseWriteableAbstract
    {
        private AGVSDbContext agvsDb;
        private SemaphoreSlim taskTbModifyLock;
        private Logger logger = LogManager.GetLogger("TaskOrderDatabaseModifier");
        public clsTaskDatabaseWriteableAbstract()
        {
        }
        public clsTaskDatabaseWriteableAbstract(AGVSDbContext agvsDb, SemaphoreSlim taskTbModifyLock)
        {
            this.agvsDb = agvsDb;
            this.taskTbModifyLock = taskTbModifyLock;
        }

        public async Task ModifyOrder(clsTaskDto dto)
        {
            try
            {
                await taskTbModifyLock.WaitAsync();
                if (dto == null)
                    return;
                MapperConfiguration config = new(cfg => cfg.CreateMap<clsTaskDto, clsTaskDto>());
                IMapper mapper = config.CreateMapper();
                var entity = agvsDb.Tasks.FirstOrDefault(tk => tk.TaskName == dto.TaskName);
                if (entity != null)
                {
                    mapper.Map(dto, entity);
                    int save_cnt = await agvsDb.SaveChangesAsync();
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

    }
}

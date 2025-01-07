using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment.AGV;
using AutoMapper;
using Microsoft.EntityFrameworkCore;

namespace VMSystem.AGV
{
    public class DeepChargeRecorder
    {
        private string taskName;
        private readonly IAGV vehicle;
        private static SemaphoreSlim dbTableLock = new SemaphoreSlim(1, 1);
        private static readonly IMapper _mapper;
        private DeepChargeRecord? record;
        AGVSDatabase agvsDb;

        static DeepChargeRecorder()
        {
            MapperConfiguration mapperConfiguration = new(cfg => cfg.CreateMap<DeepChargeRecord, DeepChargeRecord>());
            _mapper = mapperConfiguration.CreateMapper();
        }

        public DeepChargeRecorder(IAGV vehicle, string taskName)
        {
            this.vehicle = vehicle;
            this.taskName = taskName;
            agvsDb = new AGVSDatabase();
        }

        internal void StartDeepChargeOrder()
        {
        }

        internal async Task StartDeepCharge()
        {
            try
            {
                await dbTableLock.WaitAsync();
                record = await agvsDb.tables.DeepChargeRecords.AsNoTracking().FirstOrDefaultAsync(dcr => dcr.TaskID == taskName);
                if (record == null)
                    return;
                record.StartTime = DateTime.Now;
                record.OrderStatus = AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.NAVIGATING;
                record.BeginBatLv = vehicle.states.Electric_Volume[0];
                record.BeginVoltage = 0;
                await UpdateRecordToDatabase();
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                dbTableLock.Release();
            }
        }
        internal async Task EndDeepCharge(DeepChargeRecord.DEEP_CHARGE_TRIGGER_MOMENT endMoment)
        {
            try
            {
                await dbTableLock.WaitAsync();
                if (record == null)
                    return;
                record.OrderStatus = AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.ACTION_FINISH;
                record.FinalBatLv = vehicle.states.Electric_Volume[0];
                record.FinalVoltage = 0;
                record.EndTime = DateTime.Now;
                record.EndBy = endMoment;
                record.ChargeTime = (record.EndTime - record.StartTime).TotalSeconds;
                await UpdateRecordToDatabase();
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                dbTableLock.Release();
                agvsDb.Dispose();
            }
        }
        private async Task UpdateRecordToDatabase()
        {
            var _recordEntiry = await agvsDb.tables.DeepChargeRecords.FirstOrDefaultAsync(dcr => dcr.TaskID == taskName);
            if (_recordEntiry == null)
                return;
            _mapper.Map(record, _recordEntiry);
            agvsDb.tables.Entry(_recordEntiry).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
            await agvsDb.SaveChanges();
        }
    }
}


using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Maintainance;
using AGVSystemCommonNet6.Notify;

namespace VMSystem.Services
{
    public class VehicleMaintainService
    {
        private AGVSDbContext dbContext;
        public VehicleMaintainService(AGVSDbContext dbContext)
        {
            this.dbContext = dbContext;
        }

        internal List<VehicleMaintain> GetAllMaintainSettings()
        {
            return dbContext.VehicleMaintain.ToList();
        }

        internal async Task<bool> ResetCurrentValue(string agvName, MAINTAIN_ITEM maintainItem)
        {
            var existSetting = dbContext.VehicleMaintain.FirstOrDefault(state => state.AGV_Name == agvName && state.MaintainItem == maintainItem);
            if (existSetting != null)
            {
                existSetting.currentValue = 0;
                int changed = await dbContext.SaveChangesAsync();
                return true;
            }
            else
                return false;
        }

        internal async Task<bool> SettingMaintainValue(string agvName, MAINTAIN_ITEM maintainItem, double value)
        {
            var existSetting = dbContext.VehicleMaintain.FirstOrDefault(state => state.AGV_Name == agvName && state.MaintainItem == maintainItem);
            if (existSetting != null)
            {
                existSetting.maintainValue = value;
                int changed = await dbContext.SaveChangesAsync();
                return true;
            }
            else
                return false;
        }

        internal async Task UpdateHorizonMotorCurrentMileageValue(string name, double diffValue)
        {
            var existState = dbContext.VehicleMaintain.FirstOrDefault(state => state.AGV_Name == name && state.MaintainItem == MAINTAIN_ITEM.HORIZON_MOTOR);
            if (existState != null)
            {
                double currentValue = existState.currentValue + diffValue;
                existState.currentValue = currentValue;
                await dbContext.SaveChangesAsync();
                NotifyServiceHelper.INFO("Update-Maintain-State", false);
                if (existState.IsReachMaintainValue)
                {
                    NotifyServiceHelper.WARNING($"{name} 走行馬達里程已達保養里程!");
                }

            }
        }
    }
}

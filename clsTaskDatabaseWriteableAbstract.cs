using AGVSystemCommonNet6.TASK;

namespace VMSystem
{
    public abstract class clsTaskDatabaseWriteableAbstract
    {
        public static event EventHandler<clsTaskDto> OnTaskDBChangeRequestRaising;
        public void RaiseTaskDtoChange(object sender, clsTaskDto clsTaskDto)
        {
            if (OnTaskDBChangeRequestRaising != null)
                OnTaskDBChangeRequestRaising(sender, clsTaskDto);
        }

    }
}

using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using VMSystem.AGV.TaskDispatch.Tasks;

namespace VMSystem.Services
{
    public static class AGVSServicesTool
    {
        /// <summary>
        /// Unload: MoveToDestineTask+UnloadAtDestineTask
        /// Load: MoveToDestineTask+LoadAtTransferStationTask or LoadAtDestineTask
        /// Carry: MoveToSourceTask+UnloadAtSourceTask+MoveToDestineTask+LoadAtTransferStationTask or LoadAtDestineTask
        /// </summary>
        /// <param name="tag"></param>
        /// <param name="TasksObj"></param>
        /// <param name="OrderDataAction"></param>
        /// <returns></returns>
        public static async Task<clsAGVSTaskReportResponse> LoadUnloadActionStartReport(int tag, object TasksObj, ACTION_TYPE OrderDataAction)
        {
            clsAGVSTaskReportResponse response = new clsAGVSTaskReportResponse();

            if (TasksObj.GetType() == typeof(MoveToDestineTask))
            {
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Load;
                //response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(tag, OrderDataAction);
            }
            else if (TasksObj.GetType() == typeof(UnloadAtDestineTask))
            {
                //response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(tag, OrderDataAction);
            }
            else if (TasksObj.GetType() == typeof(LoadAtTransferStationTask) || TasksObj.GetType() == typeof(LoadAtDestineTask))
            {
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Load;
            }
            else if (TasksObj.GetType() == typeof(MoveToSourceTask))
            {
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Unload;
            }
            else if (TasksObj.GetType() == typeof(UnloadAtSourceTask))
            {
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Unload;
            }
            response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(tag, OrderDataAction);
            return response;
        }
    }
}

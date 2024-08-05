using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using AGVSystemCommonNet6.Notify;
using VMSystem.AGV.TaskDispatch.Tasks;
using static SQLite.SQLite3;

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
            int intSlot = -1;
            clsAGVSTaskReportResponse response = new clsAGVSTaskReportResponse();
            if (TasksObj.GetType() == typeof(MoveToDestineTask))
            {
                if (OrderDataAction == ACTION_TYPE.Charge || OrderDataAction == ACTION_TYPE.None)
                    return new clsAGVSTaskReportResponse() { confirm = true };
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Load;
            }
            else if (TasksObj.GetType() == typeof(UnloadAtDestineTask))
            { }
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
            response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(tag, 0, OrderDataAction);
            return response;
        }
        public static async Task<clsAGVSTaskReportResponse> LoadUnloadActionStartReport(clsTaskDto taskData, object TasksObj)
        {
            ACTION_TYPE OrderDataAction = taskData.Action;

            int intTag = -1;
            int intSlot = -1;
            clsAGVSTaskReportResponse response = new clsAGVSTaskReportResponse();
            if (TasksObj.GetType() == typeof(MoveToDestineTask))
            {
                if (OrderDataAction == ACTION_TYPE.Charge || OrderDataAction == ACTION_TYPE.None)
                    return new clsAGVSTaskReportResponse() { confirm = true };
                if (taskData.need_change_agv == true)
                {
                    intTag = taskData.TransferToTag;
                    intSlot = 0;
                }
                else
                {
                    intTag = taskData.To_Station_Tag;
                    intSlot = Convert.ToInt16(taskData.To_Slot);
                }
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Load;
            }
            else if (TasksObj.GetType() == typeof(LoadAtTransferStationTask) || TasksObj.GetType() == typeof(LoadAtDestineTask))
            {
                if (taskData.need_change_agv == true)
                {
                    intTag = taskData.TransferToTag;
                    intSlot = 0;
                }
                else
                {
                    intTag = taskData.To_Station_Tag;
                    intSlot = Convert.ToInt16(taskData.To_Slot);
                }
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Load;
            }
            else if (TasksObj.GetType() == typeof(UnloadAtDestineTask))
            {
                intTag = taskData.To_Station_Tag;
                intSlot = Convert.ToInt16(taskData.To_Slot);
            }
            else if (TasksObj.GetType() == typeof(MoveToSourceTask))
            {
                intTag = taskData.From_Station_Tag;
                intSlot = Convert.ToInt16(taskData.From_Slot);
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Unload;
            }
            else if (TasksObj.GetType() == typeof(UnloadAtSourceTask))
            {
                intTag = taskData.From_Station_Tag;
                intSlot = Convert.ToInt16(taskData.From_Slot);
                if (OrderDataAction == ACTION_TYPE.Carry)
                    OrderDataAction = ACTION_TYPE.Unload;
            }
            response = await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(intTag, intSlot, OrderDataAction);

            if (!response.confirm)
            {
                AlarmManagerCenter.AddAlarmAsync(response.AlarmCode, Equipment_Name: taskData.DesignatedAGVName, taskName: taskData.TaskName, level: ALARM_LEVEL.WARNING);
            }
            string agvname = taskData.DesignatedAGVName;
            //NotifyServiceHelper.INFO($"{agvname} {taskData.Action} Action Start Report To AGVS. Alarm Code Response={response.AlarmCode}");
            return response;
        }
    }
}

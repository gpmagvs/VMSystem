using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Tools.Database;
using Microsoft.EntityFrameworkCore;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch;

namespace VMSystem.VMS
{
    public class clsOptimizeAGVDispatcher : clsAGVTaskDisaptchModule
    {
        /// <summary>
        /// 取得沒有指定AGV的任務
        /// </summary>
        public override List<clsTaskDto> taskList
        {
            get
            {
                return TaskDBHelper.GetALLInCompletedTask().FindAll(f => f.State == TASK_RUN_STATUS.WAIT && f.DesignatedAGVName == "");
            }
        }
        protected override void TaskAssignWorker()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await Task.Delay(1000);
                        List<string> List_TaskAGV = new List<string>();
                        using (var database = new AGVSDatabase())
                        {
                            taskList = database.tables.Tasks.AsNoTracking().Where(f => (f.State == TASK_RUN_STATUS.WAIT) && f.DesignatedAGVName == "").OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
                            List_TaskAGV= database.tables.Tasks.AsNoTracking().Where(task => (int)task.State < 6).Select(task => task.DesignatedAGVName).Distinct().ToList();
                        }
                        if (taskList.Count == 0)
                            continue;

                        //將任務依照優先度排序
                        var taskOrderedByPriority = taskList.OrderByDescending(task => task.Priority);
                        var _taskDto = taskOrderedByPriority.First();
                        if (_taskDto.DesignatedAGVName != "")
                            continue;
                        IAGV AGV = GetOptimizeAGVToExecuteTask(_taskDto,List_TaskAGV);
                        if (AGV == null)
                            continue;

                        agv = AGV;
                        _taskDto.DesignatedAGVName = AGV.Name;
                        TaskStatusTracker.RaiseTaskDtoChange(this, _taskDto);
                        //ExecuteTaskAsync(ExecutingTask);
                    }
                    catch (Exception ex)
                    {
                        LOG.ERROR(ex);
                        continue;
                    }

                }
            });
        }

        /// <summary>
        /// 尋找最佳的AGV
        /// 策略: 如果是取放貨、 找離目的地最近的車
        /// 如果是搬運任務，找離起點最近的車
        /// </summary>
        /// <param name="taskDto"></param>
        /// <returns></returns>
        private IAGV GetOptimizeAGVToExecuteTask(clsTaskDto taskDto,List<string> List_ExceptAGV)
        {
            MapPoint refStation = null;
            //取 放貨
            if (taskDto.Action == ACTION_TYPE.Load | taskDto.Action == ACTION_TYPE.LoadAndPark | taskDto.Action == ACTION_TYPE.Unload)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out refStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out refStation);
            }

            var agvSortedByDistance = VMSManager.AllAGV.Where(agv=>agv.online_state == clsEnums.ONLINE_STATE.ONLINE).OrderBy(agv => refStation.CalculateDistance(agv.states.Coordination.X, agv.states.Coordination.Y)).OrderByDescending(agv => agv.online_state);
            var AGVListRemoveTaskAGV = agvSortedByDistance.Where(item => !List_ExceptAGV.Contains(item.Name));
            if (AGVListRemoveTaskAGV.Count() == 0)
            {
                return null;
            }
            if (AGVListRemoveTaskAGV.Count() > 1)
            {
                if (AGVListRemoveTaskAGV.All(agv => agv.main_state == clsEnums.MAIN_STATUS.RUN))
                    return AGVListRemoveTaskAGV.FirstOrDefault();
                else
                {
                    return AGVListRemoveTaskAGV.FirstOrDefault(agv => agv.main_state == clsEnums.MAIN_STATUS.IDLE||agv.main_state == clsEnums.MAIN_STATUS.Charging);
                }
            }
            return AGVListRemoveTaskAGV.First();
        }
    }
}

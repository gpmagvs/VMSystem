using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using AGVSystemCommonNet6.Tools.Database;
using VMSystem.AGV;

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
                return dbHelper.GetALLInCompletedTask().FindAll(f => f.State == TASK_RUN_STATUS.WAIT && f.DesignatedAGVName == "");
            }
        }
        protected override void TaskAssignWorker()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000);

                    if (taskList.Count == 0)
                        continue;

                    //將任務依照優先度排序
                    var taskOrderedByPriority = taskList.OrderByDescending(task => task.Priority);
                    var _taskDto = taskOrderedByPriority.First();

                    IAGV AGV = GetOptimizeAGVToExecuteTask(_taskDto);
                    agv = AGV;
                    _taskDto.DesignatedAGVName = AGV.Name;
                    UpdateTaskDtoData(ref _taskDto, TASK_RUN_STATUS.WAIT);
                    //ExecuteTaskAsync(ExecutingTask);
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
        private IAGV GetOptimizeAGVToExecuteTask(clsTaskDto taskDto)
        {
            MapStation refStation = null;
            //取 放貨
            if (taskDto.Action == ACTION_TYPE.Load | taskDto.Action == ACTION_TYPE.LoadAndPark | taskDto.Action == ACTION_TYPE.Unload)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out refStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out refStation);
            }

            var agvSortedByDistance = VMSManager.AllAGV.OrderBy(agv => refStation.CalculateDistance(agv.states.Coordination.X, agv.states.Coordination.Y));
            return agvSortedByDistance.First();
        }

        protected override void UpdateTaskDtoData(ref clsTaskDto executingTask, TASK_RUN_STATUS state)
        {
            executingTask.From_Station = agv.states.Last_Visited_Node.ToString();
            executingTask.State = state;
            dbHelper.Update(executingTask);
        }
    }
}

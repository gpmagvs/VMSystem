using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Eventing.Reader;
using VMSystem.AGV;
using VMSystem.TrafficControl;

namespace VMSystem.VMS
{
    public class clsOptimizeAGVDispatcher : clsAGVTaskDisaptchModule
    {   
        public void Run()
        {
            TaskAssignWorker();
        }

        protected override async Task TaskAssignWorker()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await Task.Delay(100);
                        
                        List<string> List_TaskAGV = new List<string>();

                        var _taskList_waiting_and_no_DesignatedAGV = DatabaseCaches.TaskCaches.WaitExecuteTasks.Where(f => (f.State == TASK_RUN_STATUS.WAIT) && f.DesignatedAGVName == "").OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
                        List<string> _taskList_for_waiting_agv = DatabaseCaches.TaskCaches.WaitExecuteTasks.Where(f => f.State == TASK_RUN_STATUS.WAIT).Select(task => task.DesignatedAGVName).Distinct().ToList();
                        List<string> _taskList_for_navigation_agv = DatabaseCaches.TaskCaches.WaitExecuteTasks.Where(f => f.State == TASK_RUN_STATUS.NAVIGATING).Select(task => task.DesignatedAGVName).Distinct().ToList();
                        List_TaskAGV.AddRange(_taskList_for_waiting_agv);
                        List_TaskAGV.AddRange(_taskList_for_navigation_agv);

                        List<string> List_idlecarryAGV = VMSManager.AllAGV.Where(agv => agv.states.AGV_Status == clsEnums.MAIN_STATUS.IDLE && (agv.states.Cargo_Status == 1 || agv.states.CSTID.Any(id => id != string.Empty))).Select(agv => agv.Name).ToList();
                        List_TaskAGV.AddRange(List_idlecarryAGV);

                        if (_taskList_waiting_and_no_DesignatedAGV.Count == 0)
                            continue;

                        //將任務依照優先度排序
                        var taskOrderedByPriority = _taskList_waiting_and_no_DesignatedAGV.OrderBy(t => t.RecieveTime.Ticks).OrderByDescending(task => task.Priority);
                        var _taskDto = taskOrderedByPriority.First();
                        if (_taskDto.DesignatedAGVName != "")
                            continue;
                        IAGV AGV = GetOptimizeAGVToExecuteTask(_taskDto, List_TaskAGV);
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
        private IAGV GetOptimizeAGVToExecuteTask(clsTaskDto taskDto, List<string> List_ExceptAGV)
        {
            MapPoint goalStation = null;
            //取 放貨
            if (taskDto.Action == ACTION_TYPE.Load || taskDto.Action == ACTION_TYPE.LoadAndPark || taskDto.Action == ACTION_TYPE.Unload)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out goalStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out goalStation);
            }
            //var agvSortedByDistance = VMSManager.AllAGV.Select(x=>x);
            List<IAGV> agvSortedByDistance = new List<IAGV>();
            try
            {
                //agvSortedByDistance = VMSManager.AllAGV.Where(agv => agv.online_state == clsEnums.ONLINE_STATE.ONLINE && agv.IsSolvingTrafficInterLock == false)
                //                                  .OrderBy(agv => Tools.ElevateDistanceToGoalStation(goalStation, agv))
                //                                  .OrderByDescending(agv => agv.online_state);
                List<object> temp = new List<object>();

                foreach (var agv in VMSManager.AllAGV)
                {
                    clsEnums.ONLINE_STATE online_state = agv.online_state;
                    if (online_state == clsEnums.ONLINE_STATE.OFFLINE)
                        continue;
                    bool b_IsSolvingTrafficInterLock = agv.IsSolvingTrafficInterLock;
                    if (b_IsSolvingTrafficInterLock == true)
                        continue;
                    double distance = double.MaxValue;
                    try
                    {
                        distance = Tools.ElevateDistanceToGoalStation(goalStation, agv);
                    }
                    catch (Exception e)
                    { continue; }
                    if (distance == double.MaxValue)
                        continue;
                    object[] obj = new object[2];
                    obj[0] = agv;
                    obj[1] = distance;
                    temp.Add(obj);
                }
                agvSortedByDistance.Clear();
                agvSortedByDistance.AddRange(temp.OrderBy(o => (double)(((object[])o)[1])).Select(x => (IAGV)(((object[])x)[0])));
                agvSortedByDistance.OrderByDescending(agv => agv.online_state);
            }
            catch (Exception ex)
            {
                throw;
            }

            var AGVListRemoveTaskAGV = agvSortedByDistance.Where(item => !List_ExceptAGV.Contains(item.Name));
            AGVListRemoveTaskAGV = AGVListRemoveTaskAGV.Where(item => item.states.AGV_Status != clsEnums.MAIN_STATUS.Charging || (item.states.AGV_Status == clsEnums.MAIN_STATUS.Charging));

            if ((taskDto.Action == ACTION_TYPE.Unload || taskDto.Action == ACTION_TYPE.Load || taskDto.Action == ACTION_TYPE.Carry) && taskDto.To_Station_AGV_Type != clsEnums.AGV_TYPE.Any)
            {
                AGVListRemoveTaskAGV = AGVListRemoveTaskAGV.Where(agv => agv.model == taskDto.To_Station_AGV_Type);
            }
            AGVListRemoveTaskAGV = AGVListRemoveTaskAGV.Where(agv => agv.CheckOutOrderExecutableByBatteryStatusAndChargingStatus(taskDto.Action, out string _));

            if (AGVListRemoveTaskAGV.Count() == 0)
                return null;
            if (AGVListRemoveTaskAGV.Count() > 1)
            {
                if (AGVListRemoveTaskAGV.All(agv => agv.main_state == clsEnums.MAIN_STATUS.RUN))
                    return AGVListRemoveTaskAGV.FirstOrDefault();
                else
                {
                    return AGVListRemoveTaskAGV.FirstOrDefault(agv => agv.main_state == clsEnums.MAIN_STATUS.IDLE || agv.main_state == clsEnums.MAIN_STATUS.Charging);
                }
            }
            return AGVListRemoveTaskAGV.First();
        }

    }
}

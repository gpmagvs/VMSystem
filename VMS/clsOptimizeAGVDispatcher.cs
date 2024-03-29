﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using Microsoft.EntityFrameworkCore;
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
                return TaskDBHelper.GetALLInCompletedTask().FindAll(f => f.State == TASK_RUN_STATUS.WAIT && f.DesignatedAGVName == "");
            }
        }


        public void Run()
        {
            TaskAssignWorker();
        }

        protected override async Task TaskAssignWorker()
        {
            Thread AssignThred = new Thread(async () =>
            {
                while (true)
                {
                    try
                    {
                        Thread.Sleep(400);
                        List<string> List_TaskAGV = new List<string>();
                        using (var database = new AGVSDatabase())
                        {
                            taskList = database.tables.Tasks.AsNoTracking().Where(f => (f.State == TASK_RUN_STATUS.WAIT) && f.DesignatedAGVName == "").OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
                            List_TaskAGV = database.tables.Tasks.AsNoTracking().Where(task => task.State == TASK_RUN_STATUS.NAVIGATING || task.State == TASK_RUN_STATUS.WAIT).Select(task => task.DesignatedAGVName).Distinct().ToList();
                            List<string> List_idlecarryAGV = VMSManager.AllAGV.Where(agv => agv.states.AGV_Status == clsEnums.MAIN_STATUS.IDLE && (agv.states.Cargo_Status == 1 || agv.states.CSTID.Any(id => id != string.Empty))).Select(agv => agv.Name).ToList();
                            List_TaskAGV.AddRange(List_idlecarryAGV);
                        }
                        if (taskList.Count == 0)
                            continue;

                        //將任務依照優先度排序
                        var taskOrderedByPriority = taskList.OrderBy(t => t.RecieveTime.Ticks).OrderByDescending(task => task.Priority);
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
            AssignThred.Start();
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
            MapPoint refStation = null;
            //取 放貨
            if (taskDto.Action == ACTION_TYPE.Load || taskDto.Action == ACTION_TYPE.LoadAndPark || taskDto.Action == ACTION_TYPE.Unload)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out refStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out refStation);
            }

            var agvSortedByDistance = VMSManager.AllAGV.Where(agv => agv.online_state == clsEnums.ONLINE_STATE.ONLINE && agv.IsSolvingTrafficInterLock == false).OrderBy(agv => refStation.CalculateDistance(agv.states.Coordination.X, agv.states.Coordination.Y)).OrderByDescending(agv => agv.online_state);
            var AGVListRemoveTaskAGV = agvSortedByDistance.Where(item => item.states.Electric_Volume[0] > 50).Where(item => !List_ExceptAGV.Contains(item.Name));
            AGVListRemoveTaskAGV = AGVListRemoveTaskAGV.Where(item => item.states.AGV_Status != clsEnums.MAIN_STATUS.Charging || (item.states.AGV_Status == clsEnums.MAIN_STATUS.Charging && item.states.Electric_Volume[0] > 80));

            if (taskDto.Action == ACTION_TYPE.Unload || taskDto.Action == ACTION_TYPE.Load && taskDto.To_Station_AGV_Type != clsEnums.AGV_TYPE.Any)
            {
                AGVListRemoveTaskAGV=AGVListRemoveTaskAGV.Where(agv => agv.model == taskDto.To_Station_AGV_Type);
            }


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

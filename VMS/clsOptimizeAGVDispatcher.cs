using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Eventing.Reader;
using VMSystem.AGV;
using VMSystem.Dispatch.Equipment;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{
    public class clsOptimizeAGVDispatcher : clsAGVTaskDisaptchModule
    {
        public override async Task Run()
        {
            TaskAssignWorker();
        }

        protected override async Task TaskAssignWorker()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(1000);

                    List<string> List_TaskAGV = new List<string>();

                    List<clsTaskDto> _taskList_waiting_and_no_DesignatedAGV = DatabaseCaches.TaskCaches.WaitExecuteTasks.Where(f => (f.State == TASK_RUN_STATUS.WAIT) && f.DesignatedAGVName == "").OrderBy(t => t.Priority).OrderBy(t => t.RecieveTime).ToList();
                    List<string> _taskList_for_waiting_agv_in_WaitExecuteTasks = DatabaseCaches.TaskCaches.WaitExecuteTasks.Where(f => f.State == TASK_RUN_STATUS.WAIT).Select(task => task.DesignatedAGVName).Distinct().ToList();
                    List<string> _taskList_for_navigation_agv_in_RunningTasks = DatabaseCaches.TaskCaches.RunningTasks.Select(task => task.DesignatedAGVName).Distinct().ToList();
                    List_TaskAGV.AddRange(_taskList_for_waiting_agv_in_WaitExecuteTasks);
                    List_TaskAGV.AddRange(_taskList_for_navigation_agv_in_RunningTasks);

                    List<string> List_idlecarryAGV = VMSManager.AllAGV.Where(agv => agv.states.AGV_Status == clsEnums.MAIN_STATUS.IDLE && (agv.states.Cargo_Status == 1 || agv.states.CSTID.Any(id => id != string.Empty))).Select(agv => agv.Name).ToList();
                    List_TaskAGV.AddRange(List_idlecarryAGV);

                    if (_taskList_waiting_and_no_DesignatedAGV.Count == 0)
                        continue;

                    //將任務依照優先度排序
                    List<clsTaskDto> taskOrderedByPriority = _taskList_waiting_and_no_DesignatedAGV.OrderBy(t => t.RecieveTime.Ticks).OrderByDescending(task => task.Priority).ToList();
                    for (int i = 0; i < taskOrderedByPriority.Count(); i++)
                    {
                        var _taskDto = taskOrderedByPriority[i];
                        if (_taskDto.DesignatedAGVName != "")
                            continue;
                        IAGV AGV = GetOptimizeAGVToExecuteTask(_taskDto, List_TaskAGV);
                        if (AGV == null)
                            continue;

                        agv = AGV;
                        _taskDto.DesignatedAGVName = AGV.Name;
                        TaskStatusTracker.RaiseTaskDtoChange(this, _taskDto);
                    }
                }
                catch (Exception ex)
                {
                    LOG.ERROR(ex);
                    continue;
                }
            }

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
            MapPoint FromStation = null;
            MapPoint ToStation = null;
            AGV_TYPE EQAcceptEQType = AGV_TYPE.Any;
            if (taskDto.Action == ACTION_TYPE.Unload)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out goalStation);
                EQAcceptEQType = Tools.GetEQAcceptAGVType(goalStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out FromStation);
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out ToStation);
                AGV_TYPE fromstation_agvtype = Tools.GetEQAcceptAGVType(FromStation);
                AGV_TYPE tostation_agvtype = Tools.GetEQAcceptAGVType(ToStation);
                goalStation = FromStation;

                if (fromstation_agvtype == AGV_TYPE.Any && tostation_agvtype == AGV_TYPE.Any)
                    EQAcceptEQType = fromstation_agvtype;
                else if (fromstation_agvtype == AGV_TYPE.Any && tostation_agvtype != AGV_TYPE.Any)
                    EQAcceptEQType = tostation_agvtype;
                else if (fromstation_agvtype != AGV_TYPE.Any && tostation_agvtype == AGV_TYPE.Any)
                    EQAcceptEQType = fromstation_agvtype;
                else if (fromstation_agvtype == tostation_agvtype)
                    EQAcceptEQType = fromstation_agvtype;
                else // fromstation_agvtype!=tostation_agvtype
                {
                    if (taskDto.transfer_task_stage == 0)
                    {
                        EQAcceptEQType = fromstation_agvtype;
                        taskDto.need_change_agv = true;
                        taskDto.transfer_task_stage = 1;
                    }
                    else if (taskDto.transfer_task_stage == 1) { }
                    else if (taskDto.transfer_task_stage == 2)
                    {
                        EQAcceptEQType = tostation_agvtype;
                    }
                }
            }
            List<IAGV> agvSortedByDistance = new List<IAGV>();
            try
            {
                List<object> temp = new List<object>();
                foreach (var agv in VMSManager.AllAGV)
                {
                    if (EQAcceptEQType != AGV_TYPE.Any && EQAcceptEQType != agv.model)
                        continue;
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
                    {
                        continue;
                    }
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

        /// <summary>
        ///  類似 EQTransferTaskManager.CheckEQAcceptAGVType
        /// </summary>
        /// <param name="AGV"></param>
        /// <param name="_taskDto"></param>
        /// <returns></returns>
        private async Task<clsTaskDto> ChechGenerateTransferTaskOrNot(IAGV AGV, clsTaskDto _taskDto)
        {
            AGV_TYPE to_station_agv_model = EquipmentStore.GetEQAcceptAGVType(_taskDto.To_Station_Tag);
            _taskDto.To_Station_AGV_Type = to_station_agv_model;
            if (_taskDto.Action == ACTION_TYPE.Load || _taskDto.Action == ACTION_TYPE.Carry || _taskDto.Action == ACTION_TYPE.LoadAndPark)
                if (_taskDto.To_Station_AGV_Type == AGV_TYPE.Any || _taskDto.To_Station_AGV_Type == AGV.model)
                    _taskDto.need_change_agv = false;
                else
                    _taskDto.need_change_agv = true;
            return _taskDto;
        }
    }
}

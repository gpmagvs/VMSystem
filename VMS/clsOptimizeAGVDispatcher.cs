﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.MCS;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using VMSystem.AGV;
using VMSystem.Dispatch.Equipment;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{
    public class clsOptimizeAGVDispatcher : clsAGVTaskDisaptchModule
    {
        public List<string> NoAcceptRandomCarryHotRunAGVNameList { get; set; } = new List<string>();
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

                        if (_taskDto.TaskName.ToUpper().Contains("HR_CARRY"))
                        {
                            List_TaskAGV.AddRange(NoAcceptRandomCarryHotRunAGVNameList);
                        }

                        IAGV AGV = await GetOptimizeAGVToExecuteTaskAsync(_taskDto, List_TaskAGV);
                        if (AGV == null)
                            continue;
                        else
                        {
                            agv = AGV;
                            _taskDto.DesignatedAGVName = AGV.Name;
                            _taskDto = ChechGenerateTransferTaskOrNot(AGV, ref _taskDto);
                        }


                        using (AGVSDatabase db = new AGVSDatabase())
                        {
                            var model = db.tables.Tasks.First(tk => tk.TaskName == _taskDto.TaskName);
                            model.DesignatedAGVName = AGV.Name;
                            model.need_change_agv = _taskDto.need_change_agv;
                            model.transfer_task_stage = _taskDto.transfer_task_stage;
                            await db.SaveChanges();
                        }
                        //    await MCSCIMService.TaskReporter((_taskDto, 1));
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
        private async Task<IAGV> GetOptimizeAGVToExecuteTaskAsync(clsTaskDto taskDto, List<string> List_ExceptAGV)
        {
            MapPoint goalStation = null;
            MapPoint FromStation = null;
            MapPoint ToStation = null;
            AGV_TYPE EQAcceptEQType = AGV_TYPE.Any;
            int goalSlotHeight = 0;
            if (taskDto.Action == ACTION_TYPE.Unload)
            {
                goalSlotHeight = int.Parse(taskDto.To_Slot);
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out goalStation);
                EQAcceptEQType = Tools.GetStationAcceptAGVType(goalStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out FromStation);
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out ToStation);
                AGV_TYPE fromstation_agvtype = Tools.GetStationAcceptAGVType(FromStation);
                AGV_TYPE tostation_agvtype = Tools.GetStationAcceptAGVType(ToStation);
                goalStation = FromStation;
                goalSlotHeight = int.Parse(taskDto.From_Slot);

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

                IEnumerable<IAGV> agvCandicators = VMSManager.AllAGV.Where(agv => (EQAcceptEQType == AGV_TYPE.Any || agv.model == EQAcceptEQType) && agv.online_state == ONLINE_STATE.ONLINE && !agv.IsSolvingTrafficInterLock);

                ConcurrentDictionary<IAGV, double> agvDistance = new ConcurrentDictionary<IAGV, double>();

                List<Task> calculateDistanceTasks = new List<Task>();
                foreach (var _agv in agvCandicators)
                {
                    calculateDistanceTasks.Add(Task.Run(() =>
                    {
                        double distance = double.MaxValue;
                        try
                        {
                            distance = Tools.ElevateDistanceToGoalStation(goalStation, goalSlotHeight, _agv);
                        }
                        catch (Exception e)
                        {
                            distance = double.MaxValue;
                        }
                        agvDistance.TryAdd(_agv, distance);
                    }));
                }
                await Task.WhenAll(calculateDistanceTasks.ToArray());
                agvSortedByDistance = agvDistance.OrderByDescending(agv => agv.Key.online_state)
                                               .Select(kp => kp.Key)
                                               .ToList();

                //foreach (var agv in VMSManager.AllAGV)
                //{
                //    if (EQAcceptEQType != AGV_TYPE.Any && EQAcceptEQType != agv.model)
                //        continue;
                //    clsEnums.ONLINE_STATE online_state = agv.online_state;
                //    if (online_state == clsEnums.ONLINE_STATE.OFFLINE)
                //        continue;
                //    bool b_IsSolvingTrafficInterLock = agv.IsSolvingTrafficInterLock;
                //    if (b_IsSolvingTrafficInterLock == true)
                //        continue;
                //    double distance = double.MaxValue;
                //    try
                //    {
                //        distance = Tools.ElevateDistanceToGoalStation(goalStation, goalSlotHeight, agv);
                //    }
                //    catch (Exception e)
                //    {
                //        continue;
                //    }
                //    if (distance == double.MaxValue)
                //        continue;
                //    object[] obj = new object[2];
                //    obj[0] = agv;
                //    obj[1] = distance;
                //    temp.Add(obj);
                //}
                //agvSortedByDistance.Clear();
                //agvSortedByDistance.AddRange(temp.OrderBy(o => (double)(((object[])o)[1])).Select(x => (IAGV)(((object[])x)[0])));
                //agvSortedByDistance.OrderByDescending(agv => agv.online_state);
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
        /// <param name="taskDto"></param>
        /// <returns></returns>
        private clsTaskDto ChechGenerateTransferTaskOrNot(IAGV AGV, ref clsTaskDto taskDto)
        {
            MapPoint goalStation = null;
            MapPoint FromStation = null;
            MapPoint ToStation = null;
            AGV_TYPE EQAcceptEQType = AGV_TYPE.Any;
            int goalSlotHeight = 0;
            if (taskDto.Action == ACTION_TYPE.Unload)
            {
                goalSlotHeight = int.Parse(taskDto.To_Slot);
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out goalStation);
                EQAcceptEQType = Tools.GetStationAcceptAGVType(goalStation);
            }
            else if (taskDto.Action == ACTION_TYPE.Load || taskDto.Action == ACTION_TYPE.LoadAndPark)
            {
                goalSlotHeight = int.Parse(taskDto.To_Slot);
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out goalStation);
                EQAcceptEQType = Tools.GetStationAcceptAGVType(goalStation);
                if (EQAcceptEQType != AGV_TYPE.Any || EQAcceptEQType != agv.model)
                    taskDto.need_change_agv = true;
            }
            else if (taskDto.Action == ACTION_TYPE.Carry)
            {
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.From_Station), out FromStation);
                StaMap.TryGetPointByTagNumber(int.Parse(taskDto.To_Station), out ToStation);
                AGV_TYPE fromstation_agvtype = Tools.GetStationAcceptAGVType(FromStation);
                AGV_TYPE tostation_agvtype = Tools.GetStationAcceptAGVType(ToStation);
                goalStation = FromStation;
                goalSlotHeight = int.Parse(taskDto.From_Slot);

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
            return taskDto;

            //AGV_TYPE to_station_agv_model = EquipmentStore.GetEQAcceptAGVType(_taskDto.To_Station_Tag, int.Parse(_taskDto.To_Slot));
            //_taskDto.To_Station_AGV_Type = to_station_agv_model;
            //if (_taskDto.Action == ACTION_TYPE.Load || _taskDto.Action == ACTION_TYPE.Carry || _taskDto.Action == ACTION_TYPE.LoadAndPark)
            //    if (_taskDto.To_Station_AGV_Type == AGV_TYPE.Any || _taskDto.To_Station_AGV_Type == AGV.model)
            //        _taskDto.need_change_agv = false;
            //    else
            //        _taskDto.need_change_agv = true;
            //return _taskDto;
        }
    }
}

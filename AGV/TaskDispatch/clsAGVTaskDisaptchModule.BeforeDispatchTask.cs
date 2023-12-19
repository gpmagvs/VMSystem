﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using System.Collections.Generic;
using VMSystem.VMS;

namespace VMSystem.AGV
{
    public partial class clsAGVTaskDisaptchModule
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="_ExecutingTask"></param>
        /// <param name="alarm_code"></param>
        /// <returns></returns>
        private bool CheckTaskOrderContentAndTryFindBestWorkStation(clsTaskDto _ExecutingTask, out ALARMS alarm_code)
        {
            if (!IsTaskContentCorrectCheck(_ExecutingTask, out int tag, out alarm_code))
                return false;

            if (tag == -1 && (_ExecutingTask.Action == ACTION_TYPE.Park | _ExecutingTask.Action == ACTION_TYPE.Charge))
            {
                LOG.INFO($"Auto Search Optimized Workstation to {_ExecutingTask.Action}");
                if (!SearchDestineStation(_ExecutingTask.Action, out MapPoint workstation, out alarm_code))
                {
                    using (var db = new AGVSDatabase())
                    {
                        var tk = db.tables.Tasks.Where(tk => tk.TaskName == _ExecutingTask.TaskName).FirstOrDefault();
                        tk.State = TASK_RUN_STATUS.FAILURE;
                        tk.FinishTime = DateTime.Now;
                        TaskStatusTracker.RaiseTaskDtoChange(this, tk);
                    }
                    return false;
                }
                LOG.INFO($"Auto Search Workstation to {_ExecutingTask.Action} Result => {workstation.Name}(Tag:{workstation.TagNumber})");
                _ExecutingTask.To_Station = workstation.TagNumber.ToString();
            }
            return true;
        }

        private bool SearchDestineStation(ACTION_TYPE action, out MapPoint optimized_workstation, out ALARMS alarm_code)
        {
            optimized_workstation = null;
            alarm_code = ALARMS.NONE;

            var alarm_code_if_occur = action == ACTION_TYPE.Charge ? ALARMS.NO_AVAILABLE_CHARGE_PILE : ALARMS.NO_AVAILABLE_PARK_STATION;

            if (action != ACTION_TYPE.Park && action != ACTION_TYPE.Charge)
            {
                alarm_code = ALARMS.STATION_TYPE_CANNOT_USE_AUTO_SEARCH;
                return false;
            }
            var map_points = StaMap.Map.Points.Values.ToList();
            List<MapPoint> workstations = new List<MapPoint>();
            if (action == ACTION_TYPE.Park)
                workstations = StaMap.GetParkableStations();
            if (action == ACTION_TYPE.Charge)
                workstations = StaMap.GetChargeableStations(this.agv);


            var othersAGV = VMSManager.AllAGV.FindAll(agv => agv != this.agv);
            var othersAGVLocTags = othersAGV.Select(agv => agv.states.Last_Visited_Node);

            while (true)
            {
                Thread.Sleep(1);
                using (var database = new AGVSDatabase())
                {
                    var chargeTasks = database.tables.Tasks.Where(tk => tk.Action == ACTION_TYPE.Charge && (tk.State == TASK_RUN_STATUS.NAVIGATING | tk.State == TASK_RUN_STATUS.WAIT));
                    if (chargeTasks.Count() == 0)
                        break;
                    if (chargeTasks.All(tk => tk.To_Station == "-1") && chargeTasks.All(tk => tk.DesignatedAGVName != this.agv.Name))
                        continue;
                    workstations = workstations.FindAll(station => !chargeTasks.Select(tk => tk.To_Station).Contains(station.TagNumber + ""));
                    break;
                }
            }


            workstations = workstations.FindAll(point => !othersAGVLocTags.Contains(point.TagNumber));
            if (workstations.Count == 0)
            {
                alarm_code = alarm_code_if_occur;
                return false;
            }
            PathFinder _pathFinder = new PathFinder();

            Dictionary<MapPoint, double> distance_of_destine = workstations.ToDictionary(point => point, point => _pathFinder.FindShortestPath(StaMap.Map, agv.currentMapPoint, point).total_travel_distance);
            var ordered = distance_of_destine.OrderBy(kp => kp.Value);
            var _optimized_workstation = ordered.FirstOrDefault(kp => _pathFinder.FindShortestPath(StaMap.Map, this.agv.currentMapPoint, kp.Key).stations.Select(pt => pt.TagNumber).Intersect(othersAGVLocTags).Count() == 0);
            optimized_workstation = _optimized_workstation.Key == null ? ordered.First().Key : _optimized_workstation.Key;
            return true;

        }

        /// <summary>
        /// 
        /// </summary>
        private bool IsTaskContentCorrectCheck(clsTaskDto _ExecutingTask, out int tag_, out ALARMS alarm_code)
        {
            alarm_code = ALARMS.NONE;
            var _action = _ExecutingTask.Action;
            string to_station = _ExecutingTask.To_Station.Trim();
            int.TryParse(to_station, out tag_);

            //有異常的狀況
            if (tag_ < 0)
            {
                if (_action != ACTION_TYPE.Charge && _action != ACTION_TYPE.Park)
                {
                    alarm_code = ALARMS.DESTIN_TAG_IS_INVLID_FORMAT;
                }
            }
            return true;
        }
    }
}

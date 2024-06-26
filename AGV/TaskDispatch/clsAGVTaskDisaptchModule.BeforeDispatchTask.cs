using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using Microsoft.EntityFrameworkCore;
using RosSharp.RosBridgeClient.MessageTypes.Geometry;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;

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
        private async Task<(bool confrim, ALARMS alarm_code)> CheckTaskOrderContentAndTryFindBestWorkStation(clsTaskDto _ExecutingTask)
        {
            if (!IsTaskContentCorrectCheck(_ExecutingTask, out int tag, out var _alarm_code))
                return (false, _alarm_code);

            bool _isAutoSearch = tag == -1 && (_ExecutingTask.Action == ACTION_TYPE.Park || _ExecutingTask.Action == ACTION_TYPE.Charge || _ExecutingTask.Action == ACTION_TYPE.ExchangeBattery);
            if (!_isAutoSearch)
                return (true, ALARMS.NONE);
            LOG.INFO($"Auto Search Optimized Workstation to {_ExecutingTask.Action}");

            (bool confirm, MapPoint workstation, ALARMS alarm_code) = await SearchDestineStation(_ExecutingTask.Action);
            if (!confirm)
            {
                return (false, _alarm_code);
            }
            LOG.INFO($"Auto Search Workstation to {_ExecutingTask.Action} Result => {workstation.Name}(Tag:{workstation.TagNumber})");
            _ExecutingTask.To_Station = workstation.TagNumber.ToString();
            return (true, ALARMS.NONE);
        }

        protected virtual async Task<(bool confirm, MapPoint optimized_workstation, ALARMS alarm_code)> SearchDestineStation(ACTION_TYPE action)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            MapPoint optimized_workstation = null;
            ALARMS alarm_code = ALARMS.NONE;

            var alarm_code_if_occur = action == ACTION_TYPE.Charge ? ALARMS.NO_AVAILABLE_CHARGE_PILE : ALARMS.NO_AVAILABLE_PARK_STATION;

            if (action != ACTION_TYPE.Park && action != ACTION_TYPE.Charge)
            {
                alarm_code = ALARMS.STATION_TYPE_CANNOT_USE_AUTO_SEARCH;
                return (false, null, alarm_code);
            }
            var map_points = StaMap.Map.Points.Values.ToList();
            List<MapPoint> workstations = new List<MapPoint>();
            if (action == ACTION_TYPE.Park)
                workstations = StaMap.GetParkableStations();
            if (action == ACTION_TYPE.Charge)
            {
                workstations = StaMap.GetChargeableStations(this.agv);
                var response = await AGVSSerivces.TRAFFICS.GetUseableChargeStationTags(this.agv.Name);
                Console.WriteLine(stopwatch.Elapsed.TotalMilliseconds);
                stopwatch.Restart();
                if (!response.confirm)
                {
                    alarm_code = ALARMS.INVALID_CHARGE_STATION;
                    return (false, null, alarm_code);
                }
                workstations = workstations.Where(station => response.usableChargeStationTags.Contains(station.TagNumber)).ToList();
            }


            var othersAGV = VMSManager.AllAGV.FilterOutAGVFromCollection(this.agv);
            var othersAGVLocTags = othersAGV.Select(agv => agv.states.Last_Visited_Node);

            List<clsTaskDto> charge_task_assign_to_others_agv = othersAGV.SelectMany(agv => agv.taskDispatchModule.taskList)
                                                                         .Where(tk => tk.Action == ACTION_TYPE.Charge && (tk.State == TASK_RUN_STATUS.NAVIGATING || tk.State == TASK_RUN_STATUS.WAIT)).ToList();

            var charge_station_tag_assign_to_others_agv = charge_task_assign_to_others_agv.Select(tk => tk.To_Station_Tag).ToList();
            var charge_stations_tag_occupied = othersAGV.Where(agv => agv.currentMapPoint.IsCharge).Select(agv => agv.currentMapPoint.TagNumber).ToList();

            var all_using_charge_station_tags = new List<int>();
            all_using_charge_station_tags.AddRange(charge_station_tag_assign_to_others_agv);
            all_using_charge_station_tags.AddRange(charge_stations_tag_occupied);
            all_using_charge_station_tags = all_using_charge_station_tags.Distinct().ToList();

            workstations = workstations.FindAll(point => !all_using_charge_station_tags.Contains(point.TagNumber));

            if (workstations.Count == 0)
            {
                alarm_code = alarm_code_if_occur;
                return (false, null, alarm_code);
            }
            if (workstations.Count == 1)
            {
                return (true, workstations.First(), ALARMS.NONE);
            }


            List<Task<(MapPoint, double)>> _tasks = new();
            foreach (var station in workstations)
            {
                _tasks.Add(Task.Run(async () =>
                {
                    await Task.Delay(10);
                    PathFinder _pathFinder = new PathFinder();
                    double distance = _pathFinder.FindShortestPath(StaMap.Map, agv.currentMapPoint, station).total_travel_distance;
                    return (station, distance);
                }));
            }
            var distanceCalculateReuslts = await Task.WhenAll(_tasks);

            Dictionary<MapPoint, double> distance_of_destine = distanceCalculateReuslts.ToDictionary(obj => obj.Item1, obj => obj.Item2);

            var ordered = distance_of_destine.OrderBy(kp => kp.Value);
            try
            {
                List<Task<(MapPoint, clsPathInfo)>> _findPathTasks = new();

                foreach (var station in ordered)
                {
                    MapPoint ptCandicate = station.Key;
                    _findPathTasks.Add(Task.Run(() =>
                    {
                        PathFinder _pathFinder = new PathFinder();
                        var pathInfo = _pathFinder.FindShortestPath(StaMap.Map, agv.currentMapPoint, ptCandicate, new PathFinder.PathFinderOption
                        {
                            ConstrainTags = othersAGV.Select(agv => agv.currentMapPoint.TagNumber).ToList(),
                            Strategy = PathFinder.PathFinderOption.STRATEGY.MINIMAL_ROTATION_ANGLE
                        });
                        return (ptCandicate, pathInfo);
                    }));
                }
                (MapPoint, clsPathInfo)[] pathFindResults = await Task.WhenAll(_findPathTasks);
                if (pathFindResults.All(par => par.Item2 == null))
                    return (false, null, ALARMS.NO_AVAILABLE_CHARGE_PILE);
                else
                {
                    optimized_workstation = pathFindResults.First(k => k.Item2 != null).Item1;
                    return (true, optimized_workstation, ALARMS.NONE);
                }
            }
            catch (Exception ex)
            {
                LOG.ERROR(ex.Message, ex);
                return (false, null, ALARMS.NO_AVAILABLE_CHARGE_PILE);
            }
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

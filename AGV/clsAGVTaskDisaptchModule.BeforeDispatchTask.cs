using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
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
        private bool BeforeDispatchTaskWorkCheck(clsTaskDto _ExecutingTask, out ALARMS alarm_code)
        {
            if (!IsTaskContentCorrectCheck(_ExecutingTask, out int tag, out alarm_code))
                return false;

            if (tag == -1 && (_ExecutingTask.Action == ACTION_TYPE.Park | _ExecutingTask.Action == ACTION_TYPE.Charge))
            {
                if (!SearchDestineStation(_ExecutingTask.Action, out string to_station_name, out alarm_code))
                {
                    UpdateTaskDtoData(ref _ExecutingTask, TASK_RUN_STATUS.FAILURE);
                    return false;
                }

                _ExecutingTask.To_Station = to_station_name;

            }
            UpdateTaskDtoData(ref _ExecutingTask , TASK_RUN_STATUS.NAVIGATING);
            return true;
        }

        private bool SearchDestineStation(ACTION_TYPE action, out string station_name, out ALARMS alarm_code)
        {
            station_name = "";
            alarm_code = ALARMS.NONE;
            if (action != ACTION_TYPE.Park && action != ACTION_TYPE.Charge)
            {
                alarm_code = ALARMS.STATION_TYPE_CANNOT_USE_AUTO_SEARCH;
                return false;
            }
            var map_points = StaMap.Map.Points.Values.ToList();
            List<MapStation> points_found = new List<MapStation>();
            if (action == ACTION_TYPE.Park)
                points_found = StaMap.GetParkableStations();
            if (action == ACTION_TYPE.Charge)
                points_found = StaMap.GetChargeableStations();

            var currentTag = agv.states.Last_Visited_Node;
            var othersAGVLocTags = VMSManager.AllAGV.FindAll(agv => agv != this.agv).Select(agv => agv.states.Last_Visited_Node);
            points_found = points_found.FindAll(point => !othersAGVLocTags.Contains(point.TagNumber));
            StaMap.TryGetPointByTagNumber(currentTag, out MapStation currentStation);
            points_found.OrderBy(pt => pt.CalculateDistance(currentStation));
            if (points_found.Count == 0)
            {
                alarm_code = action == ACTION_TYPE.Charge ? ALARMS.NO_AVAILABLE_CHARGE_PILE :
                                             ALARMS.NO_AVAILABLE_PARK_STATION;
                return false;
            }
            station_name = points_found.First().TagNumber.ToString();
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

        /// <summary>
        /// 更新起點、接收時間、狀態等
        /// </summary>
        /// <param name="executingTask"></param>
        protected virtual void UpdateTaskDtoData(ref clsTaskDto executingTask,TASK_RUN_STATUS state )
        {
            if (executingTask.Action != ACTION_TYPE.Carry)
                executingTask.From_Station = agv.states.Last_Visited_Node.ToString();
            executingTask.RecieveTime = DateTime.Now;
            executingTask.State = state;
            dbHelper.Update(executingTask);
        }
    }
}

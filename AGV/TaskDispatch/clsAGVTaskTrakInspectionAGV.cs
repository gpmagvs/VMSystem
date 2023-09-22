using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;

namespace VMSystem.AGV.TaskDispatch
{
    public class clsAGVTaskTrakInspectionAGV : clsAGVTaskTrack
    {

        /// <summary>
        /// 移動->量測/移動->電池交換
        /// </summary>
        /// <param name="taskOrder"></param>
        /// <returns></returns>
        protected override Queue<clsSubTask> CreateSubTaskLinks(clsTaskDto taskOrder)
        {
            Queue<clsSubTask> task_links = new Queue<clsSubTask>();
            var agvPoint = StaMap.GetPointByTagNumber(AGV.currentMapPoint.TagNumber);
            if (taskOrder.Action == ACTION_TYPE.None)
            {
                var destinPoint= StaMap.GetPointByTagNumber(int.Parse(taskOrder.To_Station));
                clsSubTask move_task = new clsSubTask()
                {
                    Action = ACTION_TYPE.None,
                    Source = agvPoint,
                    Destination = destinPoint,
                };
                task_links.Enqueue(move_task);
            }
            else if (taskOrder.Action == ACTION_TYPE.Measure)
            {
                string bay_name = taskOrder.To_Station;
                if (StaMap.Map.Bays.TryGetValue(bay_name, out Bay bay))
                {
                    //移動到Bay的進入點
                    var InPointOfBay = StaMap.GetPointByTagNumber(int.Parse(bay.InPoint));
                    clsSubTask move_task = new clsSubTask()
                    {
                        Action = ACTION_TYPE.None,
                        Source = agvPoint,
                        Destination = InPointOfBay,
                    };
                    task_links.Enqueue(move_task);
                    clsSubTask measure_task = new clsSubTask()
                    {
                        Action = ACTION_TYPE.Measure,
                        Source = InPointOfBay,
                        Destination = StaMap.GetPointByTagNumber(int.Parse(bay.Points.Last()))
                    };
                    task_links.Enqueue(measure_task);
                }
                else
                {

                }
            }
            else if (taskOrder.Action == ACTION_TYPE.ExchangeBattery)
            {
                var exchangerPoint = StaMap.GetPointByTagNumber(int.Parse(taskOrder.To_Station));
                //移動到電池交換站的進入點
                var InPointOfExanger = StaMap.GetPointByTagNumber(exchangerPoint.Target.Keys.First());
                clsSubTask move_task = new clsSubTask()
                {
                    Action = ACTION_TYPE.None,
                    Source = agvPoint,
                    Destination = InPointOfExanger,
                };
                task_links.Enqueue(move_task);
                clsSubTask exchange_task = new clsSubTask()
                {
                    Action = ACTION_TYPE.Measure,
                    Source = InPointOfExanger,
                    Destination = InPointOfExanger
                };
                task_links.Enqueue(exchange_task);
            }
            return task_links;
        }
    }
}

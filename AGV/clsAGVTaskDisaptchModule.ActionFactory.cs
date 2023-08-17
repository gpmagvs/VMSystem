using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV
{
    public partial class clsAGVTaskDisaptchModule
    {

        private async Task<clsPathInfo> OptimizdPathFind(int fromTag, int toTag)
        {
            var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != this.agv);
            clsPathInfo pathInfo = new clsPathInfo();
            StaMap.TryGetPointByTagNumber(int.Parse(ExecutingTask.To_Station), out MapPoint FinalPoint);
            var option = new PathFinderOption
            {
               // ConstrainTags = otherAGVList.Select(agv => agv.currentMapPoint.TagNumber).ToList() //TODO
            };
            option.ConstrainTags.AddRange(otherAGVList.SelectMany(agv => agv.RemainTrajectory.Select(pt => pt.Point_ID)));//考慮移動路徑

            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag, option);//考慮AGV阻擋下，最短路徑

            if (pathPlanDto == null) //沒有任何路線可以行走
            {
                var shortestPathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);//不考慮AGV阻擋下，最短路徑
                if (shortestPathPlanDto == null)
                {
                    AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_BLOCKED_NO_PATH_FOR_NAVIGATOR, Equipment_Name: agv.Name, location: agv.currentMapPoint.Name);
                    throw new NoPathForNavigatorException();
                }

                List<IAGV> conflic_agv_list = otherAGVList.FindAll(agv => shortestPathPlanDto.stations.Contains(agv.currentMapPoint));//跟最優路徑有衝突的AGV

                foreach (var conflic_agv in conflic_agv_list)
                {
                    if (conflic_agv.main_state == MAIN_STATUS.IDLE)
                    {
                        List<MapPoint> constrain_map_points = new List<MapPoint>();
                        constrain_map_points.AddRange(shortestPathPlanDto.stations.ToArray());
                        constrain_map_points.Add(FinalPoint);
                        bool avoid_path_found = await TrafficControlCenter.TryMoveAGV(conflic_agv, constrain_map_points);
                        if (!avoid_path_found)
                        {
                            AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_BLOCKED_NO_PATH_FOR_NAVIGATOR, Equipment_Name: conflic_agv.Name, location: conflic_agv.currentMapPoint.Name);
                            throw new NoPathForNavigatorException();
                        }
                    }
                }

                var indexOfAgvBlocked = conflic_agv_list.FindAll(agv => agv.main_state == MAIN_STATUS.IDLE).Select(agv => shortestPathPlanDto.tags.IndexOf(agv.currentMapPoint.TagNumber) - 1);
                indexOfAgvBlocked = indexOfAgvBlocked.OrderBy(index => index);

                var waitPoints = shortestPathPlanDto.stations.FindAll(pt => indexOfAgvBlocked.Contains(shortestPathPlanDto.stations.IndexOf(pt))); //等待點(AGV會先走到等待點等待下一個點位淨空)

                foreach (var pt in waitPoints)
                {
                    pathInfo.waitPoints.Enqueue(pt);
                }
                pathInfo.stations = shortestPathPlanDto.stations;
                return pathInfo;
            }
            else
            {
                return pathPlanDto;
            }
        }

        private async Task<clsTaskDownloadData> CreateDischargeActionTaskJob(string taskName, MapPoint currentMapPoint, int task_seq)
        {
            MapPoint next_point = StaMap.Map.Points[currentMapPoint.Target.First().Key];
            int fromTag = currentMapPoint.TagNumber;
            int toTag = next_point.TagNumber;
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Discharge,
                Destination = toTag,
                Task_Name = taskName,
                Station_Type = 0,
                Task_Sequence = task_seq,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }

        private async Task<clsTaskDownloadData> CreateDischargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Discharge,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }
        private async Task<clsTaskDownloadData> CreateChargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Charge,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

        private async Task<clsTaskDownloadData> CreateLDULDTaskJob(string TaskName, ACTION_TYPE Action, MapPoint EQPoint, int to_slot, string cstID, int TaskSeq)
        {
            var fromTag = StaMap.Map.Points[EQPoint.Target.First().Key].TagNumber;
            var pathPlanDto = await OptimizdPathFind(fromTag, EQPoint.TagNumber);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = Action,
                Destination = EQPoint.TagNumber,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = EQPoint.StationType,
                Task_Sequence = TaskSeq,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            return actionData;
        }

        private async Task<clsTaskDownloadData> CreateLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Load,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            return actionData;
        }
        private async Task<clsTaskDownloadData> CreateUnLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Unload,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                CST = new clsCST[1]
                {
                    new clsCST
                    {
                         CST_ID = cstID
                    }
                }
            };
            return actionData;
        }

        private async Task<clsTaskDownloadData> CreateMoveActionTaskJob(string TaskName, MapPoint fromPoint, MapPoint toPoint, int Task_Sequence)
        {
            return await CreateMoveActionTaskJob(TaskName, fromPoint.TagNumber, toPoint.TagNumber, Task_Sequence);
        }

        private async Task<clsTaskDownloadData> CreateMoveActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.None,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }

        private async Task<clsTaskDownloadData> CreateParkActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = await OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Park,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Trajectory = PathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

    }
}

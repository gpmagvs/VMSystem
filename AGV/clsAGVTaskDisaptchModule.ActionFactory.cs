using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using VMSystem.VMS;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV
{
    public partial class clsAGVTaskDisaptchModule
    {

        /// <summary>
        /// 從指派的任務產生對應的移動動作Jobs鍊
        /// </summary>
        /// <returns></returns>
        public List<clsTaskDownloadData> CreateAGVActionJobs(clsTaskDto taskDto)
        {
            try
            {

                List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

                int currentTag = agv.states.Last_Visited_Node;

                MapPoint currentStation = GetStationByTag(currentTag); //當前Staion

                if (currentStation == null)
                {
                    return new List<clsTaskDownloadData>();
                }

                MapPoint destinStation = GetStationByTag(int.Parse(taskDto.To_Station)); //目標Station

                if (currentStation.StationType != 0)//當前位置不是一般點位(可能是STK/EQ/...)
                {
                    int destinationTag = FindSecondaryPointTag(currentStation);
                    //退出
                    clsTaskDownloadData actionData = CreateDischargeActionTaskJob(taskDto.TaskName, currentTag, destinationTag, jobs.Count);
                    jobs.Add(actionData);
                    currentTag = destinationTag;
                }

                if (taskDto.Action == ACTION_TYPE.None)
                {
                    int destinationTag = int.Parse(taskDto.To_Station);
                    clsTaskDownloadData actionData = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, destinationTag, jobs.Count);
                    if (actionData.Trajectory.Count() != 0)
                        jobs.Add(actionData);
                }
                else if (taskDto.Action == ACTION_TYPE.Charge)
                {
                    int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                    //移動到二次定位點
                    clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                    jobs.Add(moveJob);
                    //進去
                    clsTaskDownloadData jobDto;
                    clsTaskDownloadData chargeJob = CreateChargeActionTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, jobs.Count);
                    jobs.Add(chargeJob);

                }
                else if (taskDto.Action == ACTION_TYPE.Park)
                {
                    int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                    //移動到二次定位點
                    clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                    jobs.Add(moveJob);
                    //進去
                    clsTaskDownloadData jobDto;
                    clsTaskDownloadData parkJob = CreateParkActionTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, jobs.Count);
                    jobs.Add(parkJob);

                }
                else if (taskDto.Action == ACTION_TYPE.Load)
                {
                    int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                    //移動到二次定位點
                    clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                    jobs.Add(moveJob);
                    //進去
                    clsTaskDownloadData chargeJob = CreateLoadTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                    jobs.Add(chargeJob);

                }
                else if (taskDto.Action == ACTION_TYPE.Unload)
                {
                    int SecondaryPointTag = FindSecondaryPointTag(destinStation);
                    //移動到二次定位點
                    clsTaskDownloadData moveJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, SecondaryPointTag, jobs.Count);
                    jobs.Add(moveJob);
                    //進去
                    clsTaskDownloadData chargeJob = CreateUnLoadTaskJob(taskDto.TaskName, SecondaryPointTag, destinStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                    jobs.Add(chargeJob);

                }
                else if (taskDto.Action == ACTION_TYPE.Carry)
                {
                    MapPoint FromStation = GetStationByTag(int.Parse(taskDto.From_Station));
                    MapPoint ToStation = GetStationByTag(int.Parse(taskDto.To_Station));

                    int From_SecondaryPointTag = FindSecondaryPointTag(FromStation);
                    int To_SecondaryPointTag = FindSecondaryPointTag(ToStation);

                    //移動到二次定位點 A
                    clsTaskDownloadData move2AJob = CreateMoveActionTaskJob(taskDto.TaskName, currentTag, From_SecondaryPointTag, jobs.Count);
                    jobs.Add(move2AJob);
                    //進去
                    clsTaskDownloadData unloadJob = CreateUnLoadTaskJob(taskDto.TaskName, From_SecondaryPointTag, FromStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                    jobs.Add(unloadJob);

                    //移動到二次定位點 B
                    clsTaskDownloadData move2BJob = CreateMoveActionTaskJob(taskDto.TaskName, From_SecondaryPointTag, To_SecondaryPointTag, jobs.Count);
                    jobs.Add(move2BJob);
                    //進去
                    clsTaskDownloadData loadJob = CreateLoadTaskJob(taskDto.TaskName, To_SecondaryPointTag, ToStation.TagNumber, int.Parse(taskDto.To_Slot), jobs.Count, taskDto.Carrier_ID);
                    jobs.Add(loadJob);

                }
                return jobs;
            }
            catch (NoPathForNavigatorException ex)
            {



                LOG.Critical(ex);
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code);
                throw ex;
            }
            catch (MapPointNotTargetsException ex)
            {
                LOG.Critical(ex);
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code);
                throw ex;
            }
            catch (Exception ex)
            {
                LOG.Critical(ex);
                AlarmManagerCenter.AddAlarm(ALARMS.ERROR_WHEN_CREATE_NAVIGATION_JOBS_LINK, ALARM_SOURCE.AGVS, ALARM_LEVEL.ALARM);
                throw ex;
            }
        }
        //private List<clsPathInfo> OptimizdPathsFind(int fromTag, int toTag)
        //{
        //    List<clsPathInfo> pathInfos = new List<clsPathInfo>();
        //    clsPathInfo PathInfo;
        //    int _destin = toTag;
        //    while ((PathInfo = OptimizdPathFind(fromTag,
        //    _destin)).tags.Last() != toTag)
        //    {
        //        pathInfos.Add(PathInfo);
        //    }

        //}

        private clsPathInfo OptimizdPathFind(int fromTag, int toTag)
        {
            var otherAGVList = VMSManager.AllAGV.FindAll(agv => agv != this.agv);
            clsPathInfo pathInfo = new clsPathInfo();

            StaMap.TryGetPointByTagNumber(int.Parse(ExecutingTask.To_Station), out MapPoint FinalPoint);
            var option = new PathFinderOption
            {
                ConstrainTags = otherAGVList.Select(agv => agv.currentMapPoint.TagNumber).ToList()
            };
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag, option);//考慮AGV阻擋下，最短路徑
            if (pathPlanDto == null) //沒有任何路線可以行走
            {
                var shortestPathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);//不考慮AGV阻擋下，最短路徑
                List<IAGV> agvNeedToMoveRemoveFromPath = otherAGVList.FindAll(agv => shortestPathPlanDto.stations.Contains(agv.currentMapPoint));
                foreach (var agv_to_move in agvNeedToMoveRemoveFromPath)
                {
                    if (agv_to_move.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.IDLE)
                    {
                        List<MapPoint> constrain_map_points = new List<MapPoint>();
                        constrain_map_points.AddRange(shortestPathPlanDto.stations.ToArray());
                        constrain_map_points.Add(FinalPoint);
                        bool avoid_path_found = TrafficControlCenter.TryMoveAGV(agv_to_move, constrain_map_points);
                        if (avoid_path_found)
                        {
                            var confilic_point = shortestPathPlanDto.stations.FirstOrDefault(pt => pt.TagNumber == agv.states.Last_Visited_Node);
                            var conflic_point_index = shortestPathPlanDto.stations.IndexOf(confilic_point);
                            //[1,2,3,4,5]  , conflic index = 3
                            MapPoint[] path_plan = new MapPoint[conflic_point_index + 1];
                            shortestPathPlanDto.stations.CopyTo(0, path_plan, 0, path_plan.Length);
                        }
                        else
                        {
                            AlarmManagerCenter.AddAlarm(ALARMS.TRAFFIC_BLOCKED_NO_PATH_FOR_NAVIGATOR, Equipment_Name: agv_to_move.Name,location: agv_to_move.currentMapPoint.Name);
                            throw new NoPathForNavigatorException();
                        }
                    }
                }

                var indexOfAgvBlocked = agvNeedToMoveRemoveFromPath.Select(agv => shortestPathPlanDto.tags.IndexOf(agv.currentMapPoint.TagNumber) - 1);
                indexOfAgvBlocked = indexOfAgvBlocked.OrderBy(index => index);
                pathInfo.waitPoints = shortestPathPlanDto.stations.FindAll(pt => indexOfAgvBlocked.Contains(shortestPathPlanDto.stations.IndexOf(pt)));
                pathInfo.stations = shortestPathPlanDto.stations;
                return pathInfo;
            }
            else
            {
                return pathPlanDto;
            }
        }

        private clsTaskDownloadData CreateDischargeActionTaskJob(string taskName, MapPoint currentMapPoint, int task_seq)
        {
            MapPoint next_point = StaMap.Map.Points[currentMapPoint.Target.First().Key];
            int fromTag = currentMapPoint.TagNumber;
            int toTag = next_point.TagNumber;
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Discharge,
                Destination = toTag,
                Task_Name = taskName,
                Station_Type = 0,
                Task_Sequence = task_seq,
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }

        private clsTaskDownloadData CreateDischargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Discharge,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }
        private clsTaskDownloadData CreateChargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Charge,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

        private clsTaskDownloadData CreateLDULDTaskJob(string TaskName, ACTION_TYPE Action, MapPoint EQPoint, int to_slot, string cstID, int TaskSeq)
        {
            var fromTag = StaMap.Map.Points[EQPoint.Target.First().Key].TagNumber;
            var pathPlanDto = OptimizdPathFind(fromTag, EQPoint.TagNumber);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = Action,
                Destination = EQPoint.TagNumber,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = EQPoint.StationType,
                Task_Sequence = TaskSeq,
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
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

        private clsTaskDownloadData CreateLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Load,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
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
        private clsTaskDownloadData CreateUnLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Unload,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
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

        private clsTaskDownloadData CreateMoveActionTaskJob(string TaskName, MapPoint fromPoint, MapPoint toPoint, int Task_Sequence)
        {
            return CreateMoveActionTaskJob(TaskName, fromPoint.TagNumber, toPoint.TagNumber, Task_Sequence);
        }

        private clsTaskDownloadData CreateMoveActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.None,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
                TrafficInfo = pathPlanDto
            };
            return actionData;
        }

        private clsTaskDownloadData CreateParkActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = OptimizdPathFind(fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Park,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

    }
}

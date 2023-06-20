using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;

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
            List<clsTaskDownloadData> jobs = new List<clsTaskDownloadData>();

            int currentTag = agv.states.Last_Visited_Node;

            MapStation currentStation = GetStationByTag(currentTag); //當前Staion

            if (currentStation == null)
            {
                return new List<clsTaskDownloadData>();
            }

            MapStation destinStation = GetStationByTag(int.Parse(taskDto.To_Station)); //目標Station

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
                MapStation FromStation = GetStationByTag(int.Parse(taskDto.From_Station));
                MapStation ToStation = GetStationByTag(int.Parse(taskDto.To_Station));

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

        private clsTaskDownloadData CreateDischargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Discharge,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }
        private clsTaskDownloadData CreateChargeActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence, STATION_TYPE stationType = STATION_TYPE.Charge)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Charge,
                Destination = toTag,
                Height = 1,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Homing_Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }
        private clsTaskDownloadData CreateLoadTaskJob(string TaskName, int fromTag, int toTag, int to_slot, int Task_Sequence, string cstID, STATION_TYPE stationType = STATION_TYPE.STK_LD)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Load,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
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
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Unload,
                Destination = toTag,
                Height = to_slot,
                Task_Name = TaskName,
                Station_Type = stationType,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
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
        private clsTaskDownloadData CreateMoveActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.None,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

        private clsTaskDownloadData CreateParkActionTaskJob(string TaskName, int fromTag, int toTag, int Task_Sequence)
        {
            var pathPlanDto = pathFinder.FindShortestPathByTagNumber(StaMap.Map.Points, fromTag, toTag);
            clsTaskDownloadData actionData = new clsTaskDownloadData()
            {
                Action_Type = ACTION_TYPE.Park,
                Destination = toTag,
                Task_Name = TaskName,
                Station_Type = 0,
                Task_Sequence = Task_Sequence,
                Task_Simplex = $"{TaskName}-{Task_Sequence}",
                Trajectory = pathFinder.GetTrajectory(StaMap.Map.Name, pathPlanDto.stations),
            };
            return actionData;
        }

    }
}

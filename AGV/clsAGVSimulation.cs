using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.TASK;
using static System.Collections.Specialized.BitVector32;
using AGVSystemCommonNet6.AGVDispatch.Messages;

namespace VMSystem.AGV
{
    public class clsAGVSimulation
    {
        private IAGV agv => dispatcherModule.agv;
        private double batteryLevelSim = 100.0;
        private readonly clsAGVTaskDisaptchModule dispatcherModule;
        private AGVStatusDBHelper agvStateDbHelper = new AGVStatusDBHelper();
        public clsAGVSimulation(clsAGVTaskDisaptchModule dispatcherModule)
        {
            this.dispatcherModule = dispatcherModule;
            if (agv.simulationMode)
            {
                //從資料庫取得狀態數據
                List<AGVSystemCommonNet6.clsAGVStateDto> agvStates = agvStateDbHelper.GetALL();
                if (agvStates.Count > 0)
                {
                    var agv_data = agvStates.FirstOrDefault(agv => agv.AGV_Name == this.agv.Name);
                    if (agv_data != null)
                    {

                        batteryLevelSim = agv_data.BatteryLevel;
                        agv.states.Last_Visited_Node = int.Parse(agv_data.CurrentLocation);
                    }
                }
                BatterSimulation();
            }
        }
        public clsTaskDto ActionRequestHandler(clsTaskDownloadData data)
        {
            agv.states.AGV_Status = clsEnums.MAIN_STATUS.RUN ;
            MoveTask(data);
            return new clsTaskDto
            {
                State = TASK_RUN_STATUS.NAVIGATING,
                TaskName = data.Task_Name,
            };
        }

        private void MoveTask(clsTaskDownloadData data)
        {
            ACTION_TYPE action = data.Action_Type;
            var Trajectory = data.ExecutingTrajecory;
            Task move_task = Task.Run(async () =>
            {
                var stateDto = new FeedbackData
                {
                    PointIndex = 0,
                    TaskName = data.Task_Name,
                    TaskSequence = data.Task_Sequence,
                    TaskSimplex = data.Task_Simplex,
                    TimeStamp = DateTime.Now.ToString(),
                    TaskStatus = 3
                };
                dispatcherModule.TaskFeedback(stateDto); //回報任務狀態

                NewMethod(action, Trajectory, stateDto);

                if (action == ACTION_TYPE.Load | action == ACTION_TYPE.Unload)
                {
                    //模擬LDULD
                    Thread.Sleep(1000);
                    if (action == ACTION_TYPE.Load)
                    {
                        agv.states.CSTID = new string[] { "", "" };
                    }
                    else
                    {
                        agv.states.CSTID = data.CST.Select(cst => cst.CST_ID).ToArray();
                    }
                    NewMethod(action, Trajectory.Reverse().ToArray(), stateDto);
                }


                double finalTheta = Trajectory.Last().Theta;
                SimulationThetaChange(agv.states.Corrdination.Theta, finalTheta);
                agv.states.Corrdination.Theta = finalTheta;

                Thread.Sleep(500);

                if (action == ACTION_TYPE.Charge)
                    agv.states.AGV_Status = clsEnums.MAIN_STATUS.Charging;
                else
                    agv.states.AGV_Status = clsEnums.MAIN_STATUS.IDLE;

                stateDto.TaskStatus = 4;
                dispatcherModule.TaskFeedback(stateDto); //回報任務狀態

            });
        }

        private void NewMethod(ACTION_TYPE action, clsMapPoint[] Trajectory, FeedbackData stateDto)
        {
            double rotateSpeed = 10;
            double moveSpeed = 10;
            int idx = 0;
            // 移动 AGV
            foreach (clsMapPoint station in Trajectory)
            {
                int currentTag = agv.states.Last_Visited_Node;
                double currentX = agv.states.Corrdination.X;
                double currentY = agv.states.Corrdination.Y;
                double currentAngle = agv.states.Corrdination.Theta;
                int stationTag = station.Point_ID;

                if (currentTag == stationTag)
                {
                    continue;
                }
                if (action == ACTION_TYPE.None)
                {
                    double deltaX = station.X - currentX;
                    double deltaY = station.Y - currentY;
                    double targetAngle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;
                    double angleDiff = targetAngle - currentAngle;
                    angleDiff = angleDiff % 360;
                    if (angleDiff < -180)
                    {
                        angleDiff += 360;
                    }
                    else if (angleDiff > 180)
                    {
                        angleDiff -= 360;
                    }
                    double rotateTime = Math.Abs(angleDiff) / rotateSpeed; // 计算旋转时间 在 rotateTime 时间内将 AGV 旋转到目标角度
                    double moveTime = Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / moveSpeed;
                    SimulationThetaChange(agv.states.Corrdination.Theta, targetAngle);
                    agv.states.Corrdination.Theta = targetAngle;
                }
                agv.states.Corrdination.X = station.X;
                agv.states.Corrdination.Y = station.Y;
                Thread.Sleep(400);
                agv.states.Last_Visited_Node = stationTag;
                Console.WriteLine($"AGV 到達{stationTag}");
                stateDto.PointIndex = idx;
                stateDto.TaskStatus = 1;
                dispatcherModule.TaskFeedback(stateDto); //回報任務狀態
                idx += 1;
            }
        }

        private void SimulationThetaChange(double currentAngle, double targetAngle)
        {
            bool clockwise = false;
            double shortestRotationAngle = (targetAngle - currentAngle + 360) % 360;
            if (shortestRotationAngle > 180)
            {
                shortestRotationAngle = 360 - shortestRotationAngle;
                clockwise = !clockwise;
            }

            //Console.WriteLine($"Start Angle: {currentAngle} degree");
            //Console.WriteLine($"Target Angle: {targetAngle} degree");
            //Console.WriteLine($"Shortest Rotation Angle: {shortestRotationAngle} degree");
            double deltaTheta = 1.2;
            if (clockwise)//-角度
            {
                //Console.WriteLine("Rotate Clockwise");
                double rotatedAngele = 0;
                while (rotatedAngele <= shortestRotationAngle)
                {
                    agv.states.Corrdination.Theta -= deltaTheta;
                    rotatedAngele += deltaTheta;
                    Thread.Sleep(10);
                }

            }
            else
            {
                //Console.WriteLine("Rotate Counterclockwise");
                double rotatedAngele = 0;
                while (rotatedAngele <= shortestRotationAngle)
                {
                    agv.states.Corrdination.Theta += deltaTheta;
                    rotatedAngele += deltaTheta;
                    Thread.Sleep(10);
                }
            }
        }

        private void BatterSimulation()
        {



            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    if (agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.Charging)
                    {
                        if (batteryLevelSim >= 100)
                        {
                            batteryLevelSim = 100;
                        }
                        else
                        {
                            batteryLevelSim += 5; //充電模擬
                        }

                    }
                    else
                    {
                        //模擬電量衰減
                        if (agv.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                        {
                            batteryLevelSim -= 0.005;
                        }
                        else
                        {
                            batteryLevelSim -= .5;//跑貨耗電比較快
                        }
                    }
                    agv.states.Electric_Volume = new double[2] { batteryLevelSim, batteryLevelSim };
                    _ = Task.Factory.StartNew(() =>
                    {
                        agvStateDbHelper.UpdateBatteryLevel(agv.Name, batteryLevelSim, out string errMsg);
                    });
                    await Task.Delay(1000);
                }
            });
        }
    }
}

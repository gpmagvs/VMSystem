﻿using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.TASK;
using static System.Collections.Specialized.BitVector32;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Mvc.Diagnostics;

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
            if (agv.options.Simulation)
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
        public async Task<clsTaskDto> ActionRequestHandler(clsTaskDownloadData data)
        {
            agv.states.AGV_Status = clsEnums.MAIN_STATUS.RUN;
            moveCancelTokenSource?.Cancel();
            await Task.Delay(1000);
            MoveTask(data);
            return new clsTaskDto
            {
                State = TASK_RUN_STATUS.NAVIGATING,
                TaskName = data.Task_Name,
            };
        }
        clsTaskDownloadData previousTaskData;
        CancellationTokenSource moveCancelTokenSource;
        Task move_task;
        private void MoveTask(clsTaskDownloadData data)
        {
            clsMapPoint[] ExecutingTrajecory = new clsMapPoint[0];
            if (previousTaskData != null)
            {
                if (previousTaskData.Task_Name == data.Task_Name && previousTaskData.Action_Type == data.Action_Type)
                {
                    var lastPoint = previousTaskData.Trajectory.Last();
                    var remainTragjectLen = data.Trajectory.Length - previousTaskData.Trajectory.Length;
                    ExecutingTrajecory = new clsMapPoint[remainTragjectLen];
                    Array.Copy(data.Trajectory, previousTaskData.Trajectory.Length, ExecutingTrajecory, 0, remainTragjectLen);
                }
                else
                {
                    ExecutingTrajecory = data.ExecutingTrajecory;
                }
            }
            else
            {
                ExecutingTrajecory = data.ExecutingTrajecory;
            }
            previousTaskData = data;
            ACTION_TYPE action = data.Action_Type;

            move_task = Task.Run(async () =>
            {
                moveCancelTokenSource = new CancellationTokenSource();

                try
                {
                    var stateDto = new FeedbackData
                    {
                        PointIndex = 0,
                        TaskName = data.Task_Name,
                        TaskSequence = data.Task_Sequence,
                        TaskSimplex = data.Task_Simplex,
                        TimeStamp = DateTime.Now.ToString(),
                        TaskStatus = TASK_RUN_STATUS.ACTION_START
                    };
                    dispatcherModule.TaskFeedback(stateDto); //回報任務狀態
                    NewMethod(action, ExecutingTrajecory, stateDto, moveCancelTokenSource.Token);
                    if (action == ACTION_TYPE.Load | action == ACTION_TYPE.Unload)
                    {
                        //模擬LDULD
                        Thread.Sleep(1000);
                        if (action == ACTION_TYPE.Load)
                        {
                            agv.states.CSTID = new string[0];
                        }
                        else
                        {
                            agv.states.CSTID = data.CST.Select(cst => cst.CST_ID).ToArray();
                        }
                        NewMethod(action, ExecutingTrajecory.Reverse().ToArray(), stateDto, moveCancelTokenSource.Token);
                    }
                    double finalTheta = ExecutingTrajecory.Last().Theta;
                    SimulationThetaChange(agv.states.Coordination.Theta, finalTheta);
                    agv.states.Coordination.Theta = finalTheta;

                    Thread.Sleep(500);

                    StaMap.TryGetPointByTagNumber(agv.states.Last_Visited_Node, out var point);

                    if (agv.currentMapPoint.IsChargeAble())
                        agv.states.AGV_Status = clsEnums.MAIN_STATUS.Charging;
                    else
                        agv.states.AGV_Status = clsEnums.MAIN_STATUS.IDLE;

                    stateDto.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                    dispatcherModule.TaskFeedback(stateDto); //回報任務狀態

                }
                catch (Exception ex)
                {
                    move_task = null;
                }
            });
        }

        private void NewMethod(ACTION_TYPE action, clsMapPoint[] Trajectory, FeedbackData stateDto, CancellationToken cancelToken)
        {
            double rotateSpeed = 10;
            double moveSpeed = 10;
            int idx = 0;
            // 移动 AGV
            foreach (clsMapPoint station in Trajectory)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }
                int currentTag = agv.states.Last_Visited_Node;
                double currentX = agv.states.Coordination.X;
                double currentY = agv.states.Coordination.Y;
                double currentAngle = agv.states.Coordination.Theta;
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
                    SimulationThetaChange(agv.states.Coordination.Theta, targetAngle);
                    agv.states.Coordination.Theta = targetAngle;
                }
                agv.states.Coordination.X = station.X;
                agv.states.Coordination.Y = station.Y;
                Thread.Sleep(400);
                agv.states.Last_Visited_Node = stationTag;
                stateDto.PointIndex = idx;
                stateDto.TaskStatus = TASK_RUN_STATUS.NAVIGATING;
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
                    if (moveCancelTokenSource.IsCancellationRequested)
                        throw new TaskCanceledException();
                    agv.states.Coordination.Theta -= deltaTheta;
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
                    if (moveCancelTokenSource.IsCancellationRequested)
                        throw new TaskCanceledException();
                    agv.states.Coordination.Theta += deltaTheta;
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
                            batteryLevelSim -= 0.01;//跑貨耗電比較快
                        }
                        if (batteryLevelSim <= 0)
                            batteryLevelSim = 1;
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

﻿using AGVSystemCommonNet6;
using static System.Collections.Specialized.BitVector32;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using AGVSystemCommonNet6.AGVDispatch.Model;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.Abstracts.CarComponent;
using System.Xml.Linq;
using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using System.Text;
using AGVSystemCommonNet6.Log;

namespace VMSystem.AGV
{
    /// <summary>
    /// AGV模擬器
    /// </summary>
    public partial class clsAGVSimulation
    {
        private IAGV agv => dispatcherModule.agv;
        private double[] batteryLevelSim = new double[] { 100.0, 100.0 };
        private readonly clsAGVTaskDisaptchModule dispatcherModule;
        private AGVStatusDBHelper agvStateDbHelper = new AGVStatusDBHelper();
        private List<clsAGVTrafficState> TrafficState => TrafficControlCenter.DynamicTrafficState.AGVTrafficStates.Values.ToList().FindAll(_agv => _agv.AGVName != agv.Name);
        clsRunningStatus runningSTatus = new clsRunningStatus();
        public clsAGVSimulation(clsAGVTaskDisaptchModule dispatcherModule)
        {
            this.dispatcherModule = dispatcherModule;
            if (agv.options.Simulation)
            {
                //從資料庫取得狀態數據
                AGVSystemCommonNet6.clsAGVStateDto agvStates = agvStateDbHelper.GetALL().FirstOrDefault(agv => agv.AGV_Name == this.agv.Name);

                if (agvStates != null)
                {
                    runningSTatus.Last_Visited_Node = int.Parse(agvStates.CurrentLocation);
                }
                else
                {
                    runningSTatus.Last_Visited_Node = agv.options.InitTag;
                }
                var loc = StaMap.GetPointByTagNumber(runningSTatus.Last_Visited_Node);
                runningSTatus.Coordination = new clsCoordination(loc.X, loc.Y, loc.Direction);
                runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
                BatterSimulation();
                ReportRunningStatusSimulation();
            }
        }

        private void ReportRunningStatusSimulation()
        {
            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    await Task.Delay(1);
                    agv.states = runningSTatus;
                }
            });
        }

        public async Task<TaskDownloadRequestResponse> ActionRequestHandler(clsTaskDownloadData data)
        {
            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.RUN;
            moveCancelTokenSource?.Cancel();
            await Task.Delay(00);
            MoveTask(data);
            return new TaskDownloadRequestResponse
            {
                ReturnCode = TASK_DOWNLOAD_RETURN_CODES.OK
            };
        }
        clsTaskDownloadData previousTaskData;
        CancellationTokenSource moveCancelTokenSource;
        Task move_task;
        bool _waitReplanflag = false;
        bool waitReplanflag
        {
            get => _waitReplanflag;
            set
            {
                if (_waitReplanflag != value)
                {
                    _waitReplanflag = value;
                    LOG.TRACE($"{agv.Name} Replan Flag change to {value}");
                }
            }
        }
        private void MoveTask(clsTaskDownloadData data)
        {
            clsMapPoint[] ExecutingTrajecory = new clsMapPoint[0];
            if (previousTaskData != null && waitReplanflag)
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
            waitReplanflag = data.ExecutingTrajecory.Last().Point_ID.ToString() != data.Destination.ToString();

            if (ExecutingTrajecory.Length == 0)
                return;

            previousTaskData = data;
            ACTION_TYPE action = data.Action_Type;

            move_task = Task.Run(async () =>
            {
                moveCancelTokenSource = new CancellationTokenSource();
                var stateDto = new FeedbackData
                {
                    PointIndex = 0,
                    TaskName = data.Task_Name,
                    TaskSequence = data.Task_Sequence,
                    TaskSimplex = data.Task_Simplex,
                    TimeStamp = DateTime.Now.ToString(),
                    TaskStatus = data.Action_Type == ACTION_TYPE.None ? TASK_RUN_STATUS.NAVIGATING : TASK_RUN_STATUS.ACTION_START
                };
                try
                {
                    dispatcherModule.TaskFeedback(stateDto); //回報任務狀態
                    BarcodeMoveSimulation(action, ExecutingTrajecory, data.Trajectory, stateDto, moveCancelTokenSource.Token);

                    if (action == ACTION_TYPE.Load | action == ACTION_TYPE.Unload)
                    {
                        //模擬LDULD
                        Thread.Sleep(1000);
                        if (action == ACTION_TYPE.Load)
                        {
                            runningSTatus.CSTID = new string[0];
                            runningSTatus.Cargo_Status = 0;
                        }
                        else
                        {
                            runningSTatus.CSTID = data.CST.Select(cst => cst.CST_ID).ToArray();
                            runningSTatus.Cargo_Status = 1;
                        }
                        ReportTaskStateToEQSimulator(action, ExecutingTrajecory.Last().Point_ID.ToString());
                        BarcodeMoveSimulation(action, ExecutingTrajecory.Reverse().ToArray(), data.Trajectory, stateDto, moveCancelTokenSource.Token);
                    }
                    double finalTheta = ExecutingTrajecory.Last().Theta;
                    SimulationThetaChange(runningSTatus.Coordination.Theta, finalTheta);
                    runningSTatus.Coordination.Theta = finalTheta;

                    StaMap.TryGetPointByTagNumber(runningSTatus.Last_Visited_Node, out var point);

                    runningSTatus.IsCharging = agv.currentMapPoint.IsChargeAble();

                    if (runningSTatus.IsCharging)
                        runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.Charging;
                    else
                        runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;

                    stateDto.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                    dispatcherModule.TaskFeedback(stateDto); //回報任務狀態

                }
                catch (Exception ex)
                {
                    move_task = null;
                    waitReplanflag = false;
                    runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
                    stateDto.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                    dispatcherModule.TaskFeedback(stateDto); //回報任務狀態
                }
            });
        }

        private void ReportTaskStateToEQSimulator(ACTION_TYPE ActionType, string EQName)
        {
            try
            {
                var ClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ClientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    string Action = ActionType == ACTION_TYPE.Load ? "Load" : "Unload";
                    ClientSocket.Connect("127.0.0.1", 100);
                    ClientSocket.Send(Encoding.ASCII.GetBytes($"{Action},{EQName}"));
                }
                catch (Exception ex)
                {
                    ClientSocket.Dispose();
                }
                Thread.Sleep(500);
                ClientSocket.Close();
            }
            catch (Exception)
            {

            }
        }
        private void BarcodeMoveSimulation(ACTION_TYPE action, clsMapPoint[] Trajectory, clsMapPoint[] OriginTrajectory, FeedbackData stateDto, CancellationToken cancelToken)
        {
            double rotateSpeed = 10;
            double moveSpeed = 10;
            int idx = 0;
            // 移动 AGV
            foreach (clsMapPoint station in Trajectory)
            {
                if (moveCancelTokenSource.IsCancellationRequested)
                {
                    waitReplanflag = false;
                    break;
                }
                MapPoint netMapPt = StaMap.GetPointByTagNumber(station.Point_ID);

                while (TrafficControlCenter.DynamicTrafficState.GetTrafficStatusByTag(agv.Name, netMapPt.TagNumber) != clsDynamicTrafficState.TRAFFIC_ACTION.PASS)
                {
                    Console.WriteLine($"Wait {netMapPt.Name} Release");
                    Thread.Sleep(1000);
                }
                if (cancelToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }
                int currentTag = runningSTatus.Last_Visited_Node;
                double currentX = runningSTatus.Coordination.X;
                double currentY = runningSTatus.Coordination.Y;
                double currentAngle = runningSTatus.Coordination.Theta;
                int stationTag = station.Point_ID;

                if (currentTag == stationTag)
                {
                    idx += 1;
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
                    SimulationThetaChange(runningSTatus.Coordination.Theta, targetAngle);
                    runningSTatus.Coordination.Theta = targetAngle;
                }
                MoveChangeSimulation(currentX, currentY, station.X, station.Y, speed: parameters.MoveSpeed);
                //Thread.Sleep(1000);
                runningSTatus.Coordination.X = station.X;
                runningSTatus.Coordination.Y = station.Y;

                var pt = StaMap.GetPointByTagNumber(runningSTatus.Last_Visited_Node);
                string err_msg = "";

                runningSTatus.Last_Visited_Node = stationTag;

                stateDto.PointIndex = OriginTrajectory.ToList().IndexOf(station);
                //stateDto.PointIndex = idx;
                stateDto.TaskStatus = TASK_RUN_STATUS.NAVIGATING;
                int feedBackCode = dispatcherModule.TaskFeedback(stateDto).Result; //回報任務狀態

                if (feedBackCode != 0)
                {
                    if (feedBackCode == 1) //停等訊號
                    {

                    }
                }
                idx += 1;
            }
        }

        private void MoveChangeSimulation(double CurrentX, double CurrentY, double TargetX, double TargetY, double speed = 1)
        {
            double O_Distance_X = TargetX - CurrentX;
            double O_Distance_Y = TargetY - CurrentY;
            double O_Distance_All = Math.Pow(Math.Pow(O_Distance_X, 2) + Math.Pow(O_Distance_Y, 2), 0.5); //用來計算總共幾秒
            double TotalSpendTime = Math.Ceiling(O_Distance_All / speed);
            double MoveSpeed_X = O_Distance_X / TotalSpendTime;
            double MoveSpeed_Y = O_Distance_Y / TotalSpendTime;

            for (int i = 0; i < TotalSpendTime; i++)
            {
                runningSTatus.Coordination.X = CurrentX + i * MoveSpeed_X;
                runningSTatus.Coordination.Y = CurrentY + i * MoveSpeed_Y;
                Thread.Sleep((int)(1000/parameters.SpeedUpRate));
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
            double deltaTheta = 10;
            if (clockwise)//-角度
            {
                //Console.WriteLine("Rotate Clockwise");
                double rotatedAngele = 0;
                while (rotatedAngele <= shortestRotationAngle)
                {
                    if (moveCancelTokenSource.IsCancellationRequested)
                        throw new TaskCanceledException();
                    runningSTatus.Coordination.Theta -= deltaTheta;
                    rotatedAngele += deltaTheta;
                    Thread.Sleep((int)(1000/parameters.SpeedUpRate));
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
                    runningSTatus.Coordination.Theta += deltaTheta;
                    rotatedAngele += deltaTheta;
                    Thread.Sleep((int)(1000 / parameters.SpeedUpRate));
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
                        if (batteryLevelSim[1] >= 100)
                        {
                            batteryLevelSim[1] = 100;
                        }
                        else
                        {
                            batteryLevelSim[1] += 5; //充電模擬
                        }

                    }
                    else
                    {
                        //模擬電量衰減
                        if (agv.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                        {
                            batteryLevelSim[1] -= 0.005;
                        }
                        else
                        {
                            batteryLevelSim[1] -= 0.01;//跑貨耗電比較快
                        }
                        if (batteryLevelSim[1] <= 0)
                            batteryLevelSim[1] = 1;
                    }
                    runningSTatus.Electric_Volume = batteryLevelSim;
                    _ = Task.Factory.StartNew(async () =>
                    {
                        await agvStateDbHelper.UpdateBatteryLevel(agv.Name, batteryLevelSim);
                    });
                    await Task.Delay(1000);
                }
            });
        }

        internal void CancelTask()
        {
            moveCancelTokenSource?.Cancel();
            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
        }
    }
}

﻿using AGVSystemCommonNet6;
using static System.Collections.Specialized.BitVector32;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using AGVSystemCommonNet6.AGVDispatch.Model;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.MAP;
using System.Xml.Linq;
using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using System.Text;
using AGVSystemCommonNet6.Log;
using RosSharp.RosBridgeClient.MessageTypes.Moveit;
using VMSystem.Tools;
using System.Drawing;
using System.Diagnostics;

namespace VMSystem.AGV
{
    /// <summary>
    /// AGV模擬器
    /// </summary>
    public partial class clsAGVSimulation
    {
        private IAGV agv => dispatcherModule.agv;
        private double[] batteryLevelSim = new double[] { 100.0 };
        private readonly clsAGVTaskDisaptchModule dispatcherModule;
        private AGVStatusDBHelper agvStateDbHelper = new AGVStatusDBHelper();
        private List<clsAGVTrafficState> TrafficState => TrafficControlCenter.DynamicTrafficState.AGVTrafficStates.Values.ToList().FindAll(_agv => _agv.AGVName != agv.Name);
        public clsRunningStatus runningSTatus = new clsRunningStatus();
        public clsAGVSimulation() { }
        public clsAGVSimulation(clsAGVTaskDisaptchModule dispatcherModule)
        {
            this.dispatcherModule = dispatcherModule;

        }

        internal void StartSimulation()
        {
            Thread thread = new Thread(async () =>
            {
                Thread.Sleep(1000);
                Console.WriteLine($"{agv.Name}-Start AGV Simulation");
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
            });
            thread.Start();
        }

        private async Task ReportRunningStatusSimulation()
        {
            while (true)
            {
                await Task.Delay(10);
                agv.states = runningSTatus;
            }
        }

        public async Task<TaskDownloadRequestResponse> ActionRequestHandler(clsTaskDownloadData data)
        {
            try
            {
                runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.RUN;
                moveCancelTokenSource?.Cancel();
                await Task.Delay(00);
                MoveTask(data);
            }
            catch (Exception ex)
            {
                LOG.ERROR(ex.Message, ex);
            }
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
            try
            {
                if (moveCancelTokenSource != null)
                    this.moveCancelTokenSource?.Cancel();

                moveCancelTokenSource = new CancellationTokenSource();

                clsMapPoint[] ExecutingTrajecory = new clsMapPoint[0];
                LOG.WARN($"[Simulator] Received Task-{data.Task_Simplex}[{data.Task_Sequence}]");
                if (previousTaskData != null && waitReplanflag && data.Action_Type == ACTION_TYPE.None)
                {
                    if (previousTaskData.Task_Name == data.Task_Name)
                    {
                        if (previousTaskData.Trajectory.Length != 0)
                        {
                            var lastPoint = previousTaskData.Trajectory.Last();
                            var remainTragjectLen = data.Trajectory.Length - previousTaskData.Trajectory.Length + 1;
                            if (remainTragjectLen <= 0)
                            {
                                return;
                            }
                            ExecutingTrajecory = new clsMapPoint[remainTragjectLen];
                            Array.Copy(data.Trajectory, previousTaskData.Trajectory.Length - 1, ExecutingTrajecory, 0, remainTragjectLen);

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
                }
                else
                {
                    ExecutingTrajecory = data.ExecutingTrajecory;
                }
                waitReplanflag = data.ExecutingTrajecory.Last().Point_ID.ToString() != data.Destination.ToString();

                previousTaskData = data;
                ACTION_TYPE action = data.Action_Type;

                bool isCargoTransferAction = action == ACTION_TYPE.Load || action == ACTION_TYPE.Unload;
                bool isNormalMoveAction = action == ACTION_TYPE.None;

                move_task = RunnTask(data, ExecutingTrajecory, action, isCargoTransferAction, isNormalMoveAction);

            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private Task RunnTask(clsTaskDownloadData data, clsMapPoint[] ExecutingTrajecory, ACTION_TYPE action, bool isCargoTransferAction, bool isNormalMoveAction)
        {
            try
            {

                var _Task = Task.Run(async () =>
                {
                    var taskFeedbackData = new FeedbackData
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
                        await dispatcherModule.TaskFeedback(taskFeedbackData); //回報任務狀態

                        await BarcodeMoveSimulation(action, ExecutingTrajecory, data.Trajectory, taskFeedbackData, moveCancelTokenSource.Token);

                        CargoStateSimulate(action, data.CST);

                        if (isCargoTransferAction)
                            await BackToHome(data, ExecutingTrajecory, action, taskFeedbackData);//開回去設備門口

                        if (isNormalMoveAction && ExecutingTrajecory.Last().Point_ID == data.Destination)
                        {
                            double finalTheta = ExecutingTrajecory.Last().Theta;
                            SimulationThetaChange(runningSTatus.Coordination.Theta, finalTheta);
                            runningSTatus.Coordination.Theta = finalTheta;
                        }

                        StaMap.TryGetPointByTagNumber(runningSTatus.Last_Visited_Node, out var point);

                        runningSTatus.IsCharging = point.IsChargeAble();

                        if (runningSTatus.IsCharging)
                            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.Charging;
                        else
                            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;

                        taskFeedbackData.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                        Thread.Sleep(100);
                        dispatcherModule.TaskFeedback(taskFeedbackData); //回報任務狀態

                    }
                    catch (Exception ex)
                    {
                        move_task = null;
                        waitReplanflag = false;
                        runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
                        taskFeedbackData.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                        LOG.Critical(ex);
                        dispatcherModule.TaskFeedback(taskFeedbackData); //回報任務狀態
                    }
                }, moveCancelTokenSource.Token);
                return _Task;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private async Task BackToHome(clsTaskDownloadData data, clsMapPoint[] ExecutingTrajecory, ACTION_TYPE action, FeedbackData stateDto)
        {
            ReportTaskStateToEQSimulator(action, ExecutingTrajecory.Last().Point_ID.ToString());
            //返回Home
            var _backHomeTrajetory = ExecutingTrajecory.Reverse().ToArray();
            await BarcodeMoveSimulation(action, _backHomeTrajetory, data.Trajectory, stateDto, moveCancelTokenSource.Token);
        }

        private void CargoStateSimulate(ACTION_TYPE action, clsCST[] CstDataDownloaded)
        {
            if (action == ACTION_TYPE.Load || action == ACTION_TYPE.LoadAndPark)
            {
                runningSTatus.CSTID = new string[0];
                runningSTatus.Cargo_Status = 0;
            }
            else if (action == ACTION_TYPE.Unload)
            {
                runningSTatus.CSTID = CstDataDownloaded.Select(cst => cst.CST_ID).ToArray();
                runningSTatus.Cargo_Status = 1;
            }
        }

        private void ReportTaskStateToEQSimulator(ACTION_TYPE ActionType, string EQName)
        {
            try
            {
                var ClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ClientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    string Action = (ActionType == ACTION_TYPE.Load || ActionType == ACTION_TYPE.LoadAndPark) ? "Load" : "Unload";
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
        private async Task BarcodeMoveSimulation(ACTION_TYPE action, clsMapPoint[] Trajectory, clsMapPoint[] OriginTrajectory, FeedbackData stateDto, CancellationToken cancelToken)
        {
            double rotateSpeed = 10;
            double moveSpeed = 10;
            int idx = 0;
            //轉向第一個點
            if (action == ACTION_TYPE.None)
            {
                TurnToNextPoint(Trajectory, rotateSpeed, moveSpeed);
            }
            if (Trajectory[0].Point_ID == runningSTatus.Last_Visited_Node)
            {
                Trajectory = Trajectory.Skip(1).ToArray();
            }
            foreach (clsMapPoint station in Trajectory)
            {
                if (moveCancelTokenSource.IsCancellationRequested)
                {
                    waitReplanflag = false;
                    runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
                    break;
                }
                MapPoint netMapPt = StaMap.GetPointByTagNumber(station.Point_ID);

                bool isNeedTrackingTagCenter = station.Control_Mode.Dodge == 11;

                if (cancelToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }
                int currentTag = runningSTatus.Last_Visited_Node;
                double currentX = runningSTatus.Coordination.X;
                double currentY = runningSTatus.Coordination.Y;
                double currentAngle = runningSTatus.Coordination.Theta;

                int stationTag = station.Point_ID;
                var isNeedStopAndRotaionWhenReachNextPoint = DeterminNextPtIsNeedRotationOrNot(Trajectory, currentTag, out double theta);

                await MoveChangeSimulation(currentX, currentY, station.X, station.Y, speed: parameters.MoveSpeedRatio * parameters.SpeedUpRate);
                runningSTatus.Coordination.X = station.X;
                runningSTatus.Coordination.Y = station.Y;
                runningSTatus.Last_Visited_Node = stationTag;
                var pt = StaMap.GetPointByTagNumber(runningSTatus.Last_Visited_Node);

                if (action == ACTION_TYPE.None && isNeedStopAndRotaionWhenReachNextPoint)
                {
                    double targetAngle = 0;
                    if (idx + 1 == Trajectory.Length)
                        targetAngle = Trajectory.Last().Theta;
                    else
                    {
                        double deltaX = Trajectory[idx].X - Trajectory[idx + 1].X;
                        double deltaY = Trajectory[idx].Y - Trajectory[idx + 1].Y;
                        targetAngle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI + 180;
                    }
                    if (Trajectory.Last() != station)
                    {
                        SimulationThetaChange(runningSTatus.Coordination.Theta, targetAngle);
                        runningSTatus.Coordination.Theta = targetAngle;
                    }
                }
                stateDto.PointIndex = OriginTrajectory.ToList().IndexOf(station);
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

        private void TurnToNextPoint(clsMapPoint[] Trajectory, double rotateSpeed, double moveSpeed)
        {
            double targetAngle = 0;
            double currentAngle = runningSTatus.Coordination.Theta;
            if (Trajectory.Length == 1)
            {
                targetAngle = Trajectory.First().Theta;
            }
            else
            {
                int currentTag = runningSTatus.Last_Visited_Node;
                double currentX = runningSTatus.Coordination.X;
                double currentY = runningSTatus.Coordination.Y;
                var NextPt = Trajectory[1];
                double deltaX = NextPt.X - currentX;
                double deltaY = NextPt.Y - currentY;
                targetAngle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;

            }
            SimulationThetaChange(runningSTatus.Coordination.Theta, targetAngle);
            runningSTatus.Coordination.Theta = targetAngle;
        }

        private bool DeterminNextPtIsNeedRotationOrNot(clsMapPoint[] Trajectory, int currentTag, out double theta)
        {
            theta = 0;
            var currentTagPt = Trajectory.FirstOrDefault(pt => pt.Point_ID == currentTag);
            if (currentTagPt == null) //起點
            {
                if (Trajectory.Length == 1)
                    return true;

                var _nextPointCoord = new clsCoordination(Trajectory[0].X, Trajectory[0].Y, 0);
                var _forwardangle = NavigationTools.CalculationForwardAngle(runningSTatus.Coordination, _nextPointCoord);
                var _forwardangle2 = NavigationTools.CalculationForwardAngle(_nextPointCoord, new clsCoordination(Trajectory[1].X, Trajectory[1].Y, 0));
                theta = Math.Abs(_forwardangle2 - _forwardangle);
                return theta > 3;
            }
            var currentPtIndex = Trajectory.ToList().IndexOf(currentTagPt);
            var nexPtIndex = currentPtIndex + 1;
            if (nexPtIndex == Trajectory.Length) //已到終點
            {
                return true;
            }
            var nextPoint = Trajectory[nexPtIndex];
            var nextPointCoord = new clsCoordination(nextPoint.X, nextPoint.Y, 0);
            var forwardangle = NavigationTools.CalculationForwardAngle(runningSTatus.Coordination, nextPointCoord);

            var nextNextPtIndex = currentPtIndex + 2;

            try
            {
                var nextNextPoint = Trajectory[nextNextPtIndex];
                var forwardangle2 = NavigationTools.CalculationForwardAngle(nextPointCoord, new clsCoordination(nextNextPoint.X, nextNextPoint.Y, 0));
                theta = Math.Abs(forwardangle2 - forwardangle);
                return theta > 3;
            }
            catch (Exception)
            {
                return true;
            }

        }

        private async Task MoveChangeSimulation(double CurrentX, double CurrentY, double TargetX, double TargetY, double speed = 2, CancellationToken token = default)
        {
            double error_total_x = TargetX - runningSTatus.Coordination.X;
            double error_total_Y = TargetY - runningSTatus.Coordination.Y;
            double O_Distance_All = Math.Sqrt(Math.Pow(error_total_x, 2) + Math.Pow(error_total_Y, 2)); //用來計算總共幾秒
            double TotalSpendTime = O_Distance_All / speed;

            double MoveSpeed_X = error_total_x / TotalSpendTime;//m
            double MoveSpeed_Y = error_total_Y / TotalSpendTime;
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < TotalSpendTime * 1000)
            {
                runningSTatus.Coordination.X += MoveSpeed_X * 0.01;
                runningSTatus.Coordination.Y += MoveSpeed_Y * 0.01;
                if (moveCancelTokenSource != null && moveCancelTokenSource.IsCancellationRequested)
                    return;
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();

                await Task.Delay(10);
            }
            runningSTatus.Coordination.X = TargetX;
            runningSTatus.Coordination.Y = TargetY;
            runningSTatus.Odometry += O_Distance_All;
        }

        private void SimulationThetaChange(double currentAngle, double targetAngle, CancellationToken token = default)
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
            Rotation(clockwise, shortestRotationAngle);

            void Rotation(bool clockWise, double thetaToRotate)
            {

                double deltaTheta = parameters.RotationSpeed * parameters.SpeedUpRate / 10.0;
                double rotatedAngele = 0;
                Stopwatch _timer = Stopwatch.StartNew();
                while (rotatedAngele <= thetaToRotate)
                {
                    Thread.Sleep(100);
                    if (token.IsCancellationRequested)
                        token.ThrowIfCancellationRequested();
                    if (moveCancelTokenSource != null && moveCancelTokenSource.IsCancellationRequested)
                        return;
                    if (clockWise)
                        runningSTatus.Coordination.Theta -= deltaTheta;
                    else
                        runningSTatus.Coordination.Theta += deltaTheta;
                    rotatedAngele += deltaTheta;
                }
                _timer.Stop();

                var speed = thetaToRotate / _timer.ElapsedMilliseconds * 1000;
                Console.WriteLine($"Rotation Speed = {speed} 度/秒(預設={parameters.RotationSpeed})");
            }
            runningSTatus.Coordination.Theta = targetAngle;
        }

        private async Task BatterSimulation()
        {
            while (true)
            {
                if (agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.Charging)
                {
                    if (batteryLevelSim[0] >= 100)
                    {
                        batteryLevelSim[0] = 100;
                    }
                    else
                    {
                        batteryLevelSim[0] += 100 * parameters.BatteryChargeSpeed * parameters.SpeedUpRate / 3600; //充電模擬
                    }

                }
                else
                {
                    //模擬電量衰減
                    if (agv.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                    {
                        batteryLevelSim[0] -= 100 * parameters.BatteryUsed_Run * parameters.SpeedUpRate / 3600 / 2;
                    }
                    else
                    {
                        batteryLevelSim[0] -= 100 * parameters.BatteryUsed_Run * parameters.SpeedUpRate / 3600;//跑貨耗電比較快
                    }
                    if (batteryLevelSim[0] <= 0)
                        batteryLevelSim[0] = 1;
                }
                runningSTatus.Electric_Volume = batteryLevelSim;
                _ = Task.Factory.StartNew(async () =>
                {
                    await agvStateDbHelper.UpdateBatteryLevel(agv.Name, batteryLevelSim);
                });
                await Task.Delay(1000);
            }
        }

        internal void CancelTask()
        {
            SemaphoreSlim = new SemaphoreSlim(1, 1);
            waitReplanflag = false;
            moveCancelTokenSource?.Cancel();
            TaskCancelTokenSource?.Cancel();
            //runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
        }
    }
}

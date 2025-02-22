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
using RosSharp.RosBridgeClient.MessageTypes.Moveit;
using System.Diagnostics;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using AGVSystemCommonNet6.DATABASE;
using NLog;
using AGVSystemCommonNet6.Vehicle_Control.VCS_ALARM;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace VMSystem.AGV
{
    /// <summary>
    /// AGV模擬器
    /// </summary>
    public partial class clsAGVSimulation : IDisposable
    {
        Logger logger = LogManager.GetLogger("AGVSimulationLog");
        private IAGV agv => dispatcherModule.agv;
        private double[] batteryLevelSim = new double[] { 100.0 };
        private readonly clsAGVTaskDisaptchModule dispatcherModule;
        private CancellationTokenSource TaskCancelTokenSource = new CancellationTokenSource();
        private BarcodeMoveArguments _currentBarcodeMoveArgs = new BarcodeMoveArguments();
        private SemaphoreSlim SemaphoreSlim = new SemaphoreSlim(1, 1);
        private List<clsAGVTrafficState> TrafficState => TrafficControlCenter.DynamicTrafficState.AGVTrafficStates.Values.ToList().FindAll(_agv => _agv.AGVName != agv.Name);
        public clsRunningStatus runningSTatus = new clsRunningStatus();
        private double Mileage = 0;
        public clsAGVSimulation() { }
        public clsAGVSimulation(clsAGVTaskDisaptchModule dispatcherModule)
        {
            this.dispatcherModule = dispatcherModule;
        }

        internal async Task StartSimulation()
        {
            await Task.Delay(1);
            //從資料庫取得狀態數據
            AGVSystemCommonNet6.clsAGVStateDto agvStates = DatabaseCaches.Vehicle.VehicleStates.FirstOrDefault(agv => agv.AGV_Name == this.agv.Name);

            if (agvStates != null)
            {
                if (int.TryParse(agvStates.CurrentLocation, out var lastVisitedTag))
                    runningSTatus.Last_Visited_Node = lastVisitedTag;
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

        private async Task ReportRunningStatusSimulation()
        {
            while (!disposedValue)
            {
                await Task.Delay(300);
                if (DiconnectSimulation)
                    continue;
                runningSTatus.Odometry = Mileage;
                runningSTatus.AppVersion = "Simulation_1.0";
                var clone = runningSTatus.Clone();
                agv.states = clone;
            }
        }


        clsTaskDownloadData previousTaskData;
        CancellationTokenSource moveCancelTokenSource;
        Task move_task;
        bool _waitReplanflag = false;
        private bool disposedValue;

        bool waitReplanflag
        {
            get => _waitReplanflag;
            set
            {
                if (_waitReplanflag != value)
                {
                    _waitReplanflag = value;
                    logger.Trace($"{agv.Name} Replan Flag change to {value}");
                }
            }
        }

        public bool CargoReadFailSimulation { get; internal set; }
        public bool CargoReadMismatchSimulation { get; internal set; }

        public bool DiconnectSimulation { get; internal set; }

        public async Task<TaskDownloadRequestResponse> ExecuteTask(clsTaskDownloadData data)
        {
            //Console.WriteLine(data.RosTaskCommandGoal.ToJson());
            TaskCancelTokenSource.Cancel();
            TaskCancelTokenSource = new CancellationTokenSource();
            var token = TaskCancelTokenSource.Token;
            _currentBarcodeMoveArgs = CreateBarcodeMoveArgsFromAGVSOrder(data);
            _currentBarcodeMoveArgs.isIDMissmatchSimulation = CargoReadMismatchSimulation;
            _currentBarcodeMoveArgs.isIDReadFailSimulation = CargoReadFailSimulation;
            _ = Task.Run(async () =>
            {
                try
                {
                    await SemaphoreSlim.WaitAsync();
                    runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.RUN;
                    _currentBarcodeMoveArgs.Feedback.TaskStatus = data.Action_Type == ACTION_TYPE.None ? TASK_RUN_STATUS.NAVIGATING : TASK_RUN_STATUS.ACTION_START;
                    await BarcodeMove(_currentBarcodeMoveArgs, token);
                    bool _hasUnRecoveryAlarm = runningSTatus.Alarm_Code.Any(al => al.Alarm_Level == 1);
                    if (_hasUnRecoveryAlarm)
                    {
                        runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.DOWN;
                        await Task.Delay(200);
                        _currentBarcodeMoveArgs.Feedback.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                        await WaitStatusNotRunReported();
                        dispatcherModule.TaskFeedback(_currentBarcodeMoveArgs.Feedback); //回報任務狀態
                        agv.TaskExecuter.HandleVehicleTaskStatusFeedback(_currentBarcodeMoveArgs.Feedback);
                        return;
                    }

                    bool hasAction = _currentBarcodeMoveArgs.action == ACTION_TYPE.Load || _currentBarcodeMoveArgs.action == ACTION_TYPE.Unload || _currentBarcodeMoveArgs.action == ACTION_TYPE.Measure;
                    if (hasAction)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(parameters.WorkingTimeAwait));
                        int GoalTag = _currentBarcodeMoveArgs.orderTrajectory.First().Point_ID;
                        while (!await _confirmLeaveWorkStationConfirm())
                        {
                            await Task.Delay(500);
                        }
                        if (_currentBarcodeMoveArgs.action == ACTION_TYPE.Unload)
                            _CargoStateSimulate(ACTION_TYPE.Unload, "TrayUnknown", false, false);
                        else if (_currentBarcodeMoveArgs.action == ACTION_TYPE.Load)
                            _CargoStateSimulate(ACTION_TYPE.Load, "", false, false);
                        await _BackToHome(_currentBarcodeMoveArgs, token);

                        if (_currentBarcodeMoveArgs.action == ACTION_TYPE.Unload && !_currentBarcodeMoveArgs.isIDMissmatchSimulation && !_currentBarcodeMoveArgs.isIDReadFailSimulation)
                            await WaitCarrierIDReported(_currentBarcodeMoveArgs.CSTID);

                        async Task<bool> _confirmLeaveWorkStationConfirm()
                        {
                            (bool accept, string message) = await TrafficControlCenter.AGVLeaveWorkStationRequest(agv.Name, GoalTag);
                            return accept;
                        }
                    }

                    bool _isChargeAction = _currentBarcodeMoveArgs.action == ACTION_TYPE.Charge;
                    runningSTatus.IsCharging = _isChargeAction;
                    if (_hasUnRecoveryAlarm)
                        runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.DOWN;
                    else
                        runningSTatus.AGV_Status = _isChargeAction ? clsEnums.MAIN_STATUS.Charging : clsEnums.MAIN_STATUS.IDLE;
                    if (_hasUnRecoveryAlarm)
                        await Task.Delay(200);
                    _currentBarcodeMoveArgs.Feedback.PointIndex = hasAction ? 0 : _currentBarcodeMoveArgs.Feedback.PointIndex;
                    _currentBarcodeMoveArgs.Feedback.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                    await WaitStatusNotRunReported();
                    dispatcherModule.TaskFeedback(_currentBarcodeMoveArgs.Feedback); //回報任務狀態
                    agv.TaskExecuter.HandleVehicleTaskStatusFeedback(_currentBarcodeMoveArgs.Feedback);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Emu]-Previous Task Interupted.");
                }
                finally
                {
                    SemaphoreSlim.Release();
                    runningSTatus.Alarm_Code = new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode[0];
                }
                //runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;

                async Task _BackToHome(BarcodeMoveArguments _args, CancellationToken _token)
                {
                    _args.orderTrajectory = _args.orderTrajectory.Reverse();
                    _args.Feedback.PointIndex = 1;
                    _args.Feedback.TaskStatus = TASK_RUN_STATUS.NAVIGATING;
                    dispatcherModule.TaskFeedback(_args.Feedback); //回報任務狀態
                    agv.TaskExecuter.HandleVehicleTaskStatusFeedback(_args.Feedback);
                    _ = Task.Run(() => ReportTaskStateToEQSimulator(_args.action, _args.nextMoveTrajectory.First().Point_ID.ToString()));
                    await BarcodeMove(_args, _token, homing: true);
                    _CargoStateSimulate(_args.action, _args.CSTID, _args.isIDMissmatchSimulation, _args.isIDReadFailSimulation);

                }

                void _CargoStateSimulate(ACTION_TYPE action, string cstID, bool isIDMissmatchSimulation, bool isIDReadFailSimulation)
                {
                    if (action == ACTION_TYPE.Load || action == ACTION_TYPE.LoadAndPark)
                    {
                        runningSTatus.CSTID = new string[0];
                        runningSTatus.Cargo_Status = 0;
                    }
                    else if (action == ACTION_TYPE.Unload)
                    {
                        if (isIDReadFailSimulation)
                            runningSTatus.CSTID = new string[] { "error" };
                        else if (isIDMissmatchSimulation)
                            runningSTatus.CSTID = new string[] { "TAE00000001" };
                        else
                            runningSTatus.CSTID = new string[] { cstID };
                        runningSTatus.Cargo_Status = 1;
                    }
                }
            }, token);

            return new TaskDownloadRequestResponse() { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.OK };
        }

        private async Task WaitCarrierIDReported(string cSTID)
        {
            while (agv.states.CSTID.FirstOrDefault() != cSTID)
            {
                await Task.Delay(10);
            }
        }

        private async Task BarcodeMove(BarcodeMoveArguments moveArgs, CancellationToken token, bool homing = false)
        {
            try
            {

                int currentTag = runningSTatus.Last_Visited_Node;
                int currentTagIndex = moveArgs.nextMoveTrajectory.GetTagList().ToList().IndexOf(currentTag);


                moveArgs.nextMoveTrajectory = moveArgs.action == ACTION_TYPE.Measure ? moveArgs.orderTrajectory : moveArgs.orderTrajectory.Skip(currentTagIndex).ToArray();

                clsMapPoint[] Trajectory = moveArgs.nextMoveTrajectory.Count() == 1 ?
                                            moveArgs.nextMoveTrajectory.ToArray() : moveArgs.nextMoveTrajectory.Where(pt => pt.Point_ID != currentTag).ToArray();
                ACTION_TYPE action = moveArgs.action;

                var taskFeedbackData = moveArgs.Feedback;

                int feedBackCode = dispatcherModule.TaskFeedback(taskFeedbackData).Result; //回報任務狀態
                agv.TaskExecuter.HandleVehicleTaskStatusFeedback(taskFeedbackData);

                int idx = 0;
                //轉向第一個點
                if (moveArgs.action == ACTION_TYPE.None)
                {
                    await TurnToNextPoint(Trajectory);
                }
                if (Trajectory[0].Point_ID == runningSTatus.Last_Visited_Node)
                {
                    Trajectory = Trajectory.Skip(1).ToArray();
                }
                foreach (clsMapPoint station in Trajectory)
                {
                    MapPoint netMapPt = StaMap.GetPointByTagNumber(station.Point_ID);

                    bool isNeedTrackingTagCenter = station.Control_Mode.Dodge == 11;

                    if (token.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                    currentTag = runningSTatus.Last_Visited_Node;
                    double currentX = runningSTatus.Coordination.X;
                    double currentY = runningSTatus.Coordination.Y;
                    double currentAngle = runningSTatus.Coordination.Theta;

                    int stationTag = station.Point_ID;
                    var isNeedStopAndRotaionWhenReachNextPoint = DeterminNextPtIsNeedRotationOrNot(Trajectory, currentTag, out double theta);
                    var _speed = action == ACTION_TYPE.None ? parameters.MoveSpeedRatio : parameters.TapMoveSpeedRatio;
                    await MoveChangeSimulation(currentX, currentY, station.X, station.Y, speed: _speed * parameters.SpeedUpRate, token);
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
                            await SimulationThetaChange(runningSTatus.Coordination.Theta, targetAngle, token);
                            runningSTatus.Coordination.Theta = targetAngle;
                        }
                    }
                    taskFeedbackData.PointIndex = moveArgs.orderTrajectory.GetTagList().ToList().IndexOf(stationTag);
                    taskFeedbackData.TaskStatus = TASK_RUN_STATUS.NAVIGATING;
                    taskFeedbackData.LastVisitedNode = stationTag;

                    while (agv.states.Last_Visited_Node != stationTag)
                    {
                        await Task.Delay(1);
                    }

                    feedBackCode = dispatcherModule.TaskFeedback(taskFeedbackData).Result; //回報任務狀態
                    agv.TaskExecuter.HandleVehicleTaskStatusFeedback(taskFeedbackData);


                    if (action == ACTION_TYPE.Measure && !homing)
                    {
                        await MeasureSimulation(stationTag);
                    }
                    idx += 1;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (action == ACTION_TYPE.None && Trajectory.Length > 0)
                    await SimulationThetaChange(runningSTatus.Coordination.Theta, Trajectory.Last().Theta, token);

            }
            catch (Exception ex)
            {
                int indexOfCurrentTagInTraj = moveArgs.nextMoveTrajectory.ToList().FindIndex(pt => pt.Point_ID == runningSTatus.Last_Visited_Node);
                bool iscurrentEnd = indexOfCurrentTagInTraj + 1 > moveArgs.nextMoveTrajectory.Count();
                var pointToSet = iscurrentEnd ? moveArgs.nextMoveTrajectory.Last() : moveArgs.nextMoveTrajectory.ToList()[(indexOfCurrentTagInTraj + 1)];
                runningSTatus.Coordination.X = pointToSet.X;
                runningSTatus.Coordination.Y = pointToSet.Y;
                runningSTatus.Last_Visited_Node = pointToSet.Point_ID;
            }
        }

        private async Task MeasureSimulation(int stationTag)
        {
            var mapPoint = StaMap.GetPointByTagNumber(stationTag);
            await Task.Delay(3000);
            (agv.taskDispatchModule.OrderHandler as MeasureOrderHandler).MeasureResultFeedback(
                new clsMeasureResult(stationTag)
                {
                    location = mapPoint.Graph.Display,
                    AGVName = agv.Name,
                });
        }

        private BarcodeMoveArguments CreateBarcodeMoveArgsFromAGVSOrder(clsTaskDownloadData orderData)
        {
            return new BarcodeMoveArguments
            {
                TaskName = orderData.Task_Name,
                TaskSimplex = orderData.Task_Simplex,
                TaskSequence = orderData.Task_Sequence,
                action = orderData.Action_Type,
                CSTID = orderData.CST.Count() == 0 ? "" : orderData.CST.First().CST_ID,
                goal = orderData.Destination,
                nextMoveTrajectory = orderData.ExecutingTrajecory,
                orderTrajectory = orderData.ExecutingTrajecory,
                Feedback = new FeedbackData
                {
                    PointIndex = 0,
                    TaskName = orderData.Task_Name,
                    TaskSequence = orderData.Task_Sequence,
                    TaskSimplex = orderData.Task_Simplex,
                    TimeStamp = DateTime.Now.ToString(),
                }
            };
        }

        private class BarcodeMoveArguments
        {
            public string TaskName;
            public string TaskSimplex;
            public int TaskSequence;
            public string CSTID;
            public ACTION_TYPE action = ACTION_TYPE.None;
            public IEnumerable<clsMapPoint> nextMoveTrajectory = new List<clsMapPoint>();
            public IEnumerable<clsMapPoint> orderTrajectory = new List<clsMapPoint>();
            public int goal = 0;
            public bool isTrajectoryEndIsGoal => nextMoveTrajectory.Count() == 0 ? true : nextMoveTrajectory.Last().Point_ID == goal;
            public FeedbackData Feedback = new FeedbackData();
            public bool isIDMissmatchSimulation = false;
            public bool isIDReadFailSimulation = false;
        }


        private async Task ReportTaskStateToEQSimulator(ACTION_TYPE ActionType, string EQName)
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
                await Task.Delay(100);
                ClientSocket.Close();
            }
            catch (Exception)
            {

            }
        }


        private async Task TurnToNextPoint(clsMapPoint[] Trajectory)
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
            await SimulationThetaChange(runningSTatus.Coordination.Theta, targetAngle);
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
                var _forwardangle = Tools.CalculationForwardAngle(runningSTatus.Coordination, _nextPointCoord);
                var _forwardangle2 = Tools.CalculationForwardAngle(_nextPointCoord, new clsCoordination(Trajectory[1].X, Trajectory[1].Y, 0));
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
            var forwardangle = Tools.CalculationForwardAngle(runningSTatus.Coordination, nextPointCoord);

            var nextNextPtIndex = currentPtIndex + 2;

            try
            {
                var nextNextPoint = Trajectory[nextNextPtIndex];
                var forwardangle2 = Tools.CalculationForwardAngle(nextPointCoord, new clsCoordination(nextNextPoint.X, nextNextPoint.Y, 0));
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
                runningSTatus.Coordination.X += MoveSpeed_X * 0.1;
                runningSTatus.Coordination.Y += MoveSpeed_Y * 0.1;


                if (moveCancelTokenSource != null && moveCancelTokenSource.IsCancellationRequested)
                    return;
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();

                await Task.Delay(100);
            }
            runningSTatus.Coordination.X = TargetX;
            runningSTatus.Coordination.Y = TargetY;
            Mileage += O_Distance_All;
        }

        private async Task SimulationThetaChange(double currentAngle, double targetAngle, CancellationToken token = default)
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
            await Rotation(clockwise, shortestRotationAngle);

            async Task Rotation(bool clockWise, double thetaToRotate)
            {

                double deltaTheta = parameters.RotationSpeed * parameters.SpeedUpRate / 10.0;
                double _time_spend = thetaToRotate / parameters.RotationSpeed / parameters.SpeedUpRate;//時間

                double rotatedAngele = 0;
                Stopwatch _timer = Stopwatch.StartNew();
                while (_timer.ElapsedMilliseconds <= _time_spend * 1000)
                {
                    await Task.Delay(100);
                    if (token.IsCancellationRequested)
                        token.ThrowIfCancellationRequested();
                    if (moveCancelTokenSource != null && moveCancelTokenSource.IsCancellationRequested)
                        return;
                    if (clockWise)
                        runningSTatus.Coordination.Theta -= deltaTheta;
                    else
                        runningSTatus.Coordination.Theta += deltaTheta;
                    rotatedAngele += deltaTheta;
                    //Console.WriteLine($"{rotatedAngele}/{thetaToRotate}");
                }
                _timer.Stop();

                var speed_after_speed_up = thetaToRotate / _timer.ElapsedMilliseconds * 1000;
                var speed = thetaToRotate / _timer.ElapsedMilliseconds / parameters.SpeedUpRate * 1000;
                //Console.WriteLine($"Rotation Speed(加速後) = {speed_after_speed_up} 度/秒(預設={parameters.RotationSpeed})");
                //Console.WriteLine($"Rotation Speed = {speed} 度/秒(預設={parameters.RotationSpeed})");
            }
            runningSTatus.Coordination.Theta = targetAngle;
        }

        private async Task BatterSimulation()
        {

            while (!disposedValue)
            {
                if (agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.Charging)
                {
                    if (batteryLevelSim[0] >= 100)
                    {
                        batteryLevelSim[0] = 100;
                        runningSTatus.AGV_Status = MAIN_STATUS.IDLE;
                        runningSTatus.IsCharging = false;
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
                await Task.Delay(1000);
            }
        }
        internal void UnRecoveryAlarmRaise()
        {
            runningSTatus.Alarm_Code = new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode[1]
            {
                 new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode()
                 {
                      Alarm_Level =  1,
                       Alarm_Category=2,
                        Alarm_Description ="緊急停止",
                        Alarm_Description_EN="EMO",
                         Alarm_ID = 6699,
                 }
            };
            CancelTask(100);
        }

        internal void BatterySOCDistortionWarningRaise()
        {
            var currentAlarms = runningSTatus.Alarm_Code.ToList();
            currentAlarms.Add(new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode
            {
                Alarm_ID = 56780,
                Alarm_Level = 0,
                Alarm_Category = 0,
                Alarm_Description = "Battery SOC Distortion",
                Alarm_Description_EN = "Battery SOC Distortion"
            });
            runningSTatus.Alarm_Code = currentAlarms.ToArray();
        }

        internal void EMO()
        {
            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.DOWN;
            runningSTatus.Alarm_Code = new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode[1]
            {
                 new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode()
                 {
                      Alarm_Level =  1,
                       Alarm_Category=2,
                        Alarm_Description ="緊急停止",
                        Alarm_Description_EN="EMO",
                         Alarm_ID = 20,
                 }
            };
            CancelTask(delay: 10);
            agv.AGVOfflineFromAGV(out string msg);
        }
        internal void CancelTask(int delay = 1000)
        {
            Task.Run(async () =>
            {
                await Task.Delay(delay); //模擬車控走行到一半還不能馬上停下的狀況

                waitReplanflag = false;
                moveCancelTokenSource?.Cancel();
                TaskCancelTokenSource?.Cancel();

                if (runningSTatus.AGV_Status != clsEnums.MAIN_STATUS.RUN)
                {
                    _currentBarcodeMoveArgs.Feedback.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                    await WaitStatusNotRunReported();
                    agv.TaskExecuter.HandleVehicleTaskStatusFeedback(_currentBarcodeMoveArgs.Feedback);
                }
            });

            //runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
        }



        internal void Initialize()
        {
            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
            runningSTatus.Alarm_Code = new AGVSystemCommonNet6.AGVDispatch.Model.clsAlarmCode[0];
        }

        internal void MounteCargo(string cargoID)
        {
            runningSTatus.Cargo_Status = 1;
            runningSTatus.CSTID = new string[1] { cargoID };
        }

        internal void RemoveCargo()
        {
            runningSTatus.Cargo_Status = 0;
            runningSTatus.CSTID = new string[1] { "" };
        }

        private async Task WaitStatusNotRunReported()
        {
            while (agv.main_state == clsEnums.MAIN_STATUS.RUN)
            {
                await Task.Delay(100);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 處置受控狀態 (受控物件)
                }

                // TODO: 釋出非受控資源 (非受控物件) 並覆寫完成項
                // TODO: 將大型欄位設為 Null
                disposedValue = true;
            }
        }

        // // TODO: 僅有當 'Dispose(bool disposing)' 具有會釋出非受控資源的程式碼時，才覆寫完成項
        // ~clsAGVSimulation()
        // {
        //     // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        internal void SetTag(int tag)
        {
            agv.AgvSimulation.runningSTatus.Last_Visited_Node = tag;
            var _mapPoint = StaMap.GetPointByTagNumber(tag);
            if (_mapPoint != null)
            {
                agv.AgvSimulation.runningSTatus.Coordination.X = _mapPoint.X;
                agv.AgvSimulation.runningSTatus.Coordination.Y = _mapPoint.Y;
            }
            else
            {
                agv.AgvSimulation.runningSTatus.Coordination.X = -12;
                agv.AgvSimulation.runningSTatus.Coordination.Y = 0;
            }
        }

        internal bool VMSOnline(out string message)
        {
            message = "Test Online Rejected";
            bool isAMCAGV = agv.model == AGV_TYPE.INSPECTION_AGV;

            MapPoint currentPoint = StaMap.GetPointByTagNumber(runningSTatus.Last_Visited_Node);

            if (!isAMCAGV && currentPoint.IsVirtualPoint)
            {
                message = "Current_Tag_Cannot_Online_At_Virtual_Point";
                return false;
            }

            if (currentPoint.StationType != STATION_TYPE.Normal && !currentPoint.IsCharge && !_IsParkAtBuffer())
            {
                message = "Cant_Online_In_Equipment";
                return false;
            }
            if (runningSTatus.Cargo_Status == 0 && runningSTatus.CSTID.Any() && runningSTatus.CSTID.First() != "")
            {
                message = "AGV_HasIDBut_No_Cargo";
                return false;
            }

            bool _IsParkAtBuffer()
            {
                return currentPoint.StationType == STATION_TYPE.Buffer || currentPoint.StationType == STATION_TYPE.Charge_Buffer;
            }

            return true;
        }

        internal void UnCharge()
        {
            runningSTatus.IsCharging = false;
            runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.IDLE;
        }
    }
}

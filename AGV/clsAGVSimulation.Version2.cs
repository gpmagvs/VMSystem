using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV
{
    public partial class clsAGVSimulation
    {

        private CancellationTokenSource TaskCancelTokenSource = new CancellationTokenSource();
        private BarcodeMoveArguments _currentBarcodeMoveArgs = new BarcodeMoveArguments();
        public async Task<TaskDownloadRequestResponse> ExecuteTask(clsTaskDownloadData data)
        {
            TaskCancelTokenSource.Cancel();
            TaskCancelTokenSource = new CancellationTokenSource();
            var token = TaskCancelTokenSource.Token;
            try
            {
                _currentBarcodeMoveArgs = CreateBarcodeMoveArgsFromAGVSOrder(data);
                await Task.Run(async () =>
                {
                    runningSTatus.AGV_Status = clsEnums.MAIN_STATUS.RUN;
                    _currentBarcodeMoveArgs.Feedback.TaskStatus = data.Action_Type == ACTION_TYPE.None ? TASK_RUN_STATUS.NAVIGATING : TASK_RUN_STATUS.ACTION_START;

                    await BarcodeMove(_currentBarcodeMoveArgs, token);

                    if (_currentBarcodeMoveArgs.action == ACTION_TYPE.Load || _currentBarcodeMoveArgs.action == ACTION_TYPE.Unload)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(parameters.WorkingTimeAwait));
                        await _BackToHome(_currentBarcodeMoveArgs, token);
                    }

                    bool _isChargeAction = _currentBarcodeMoveArgs.action == ACTION_TYPE.Charge;
                    runningSTatus.IsCharging = _isChargeAction;
                    runningSTatus.AGV_Status = _isChargeAction ? clsEnums.MAIN_STATUS.Charging : clsEnums.MAIN_STATUS.IDLE;

                    _currentBarcodeMoveArgs.Feedback.TaskStatus = TASK_RUN_STATUS.ACTION_FINISH;
                    dispatcherModule.TaskFeedback(_currentBarcodeMoveArgs.Feedback); //回報任務狀態


                    async Task _BackToHome(BarcodeMoveArguments _args, CancellationToken _token)
                    {
                        _CargoStateSimulate(_args.action, _args.CSTID);
                        _args.orderTrajectory = _args.orderTrajectory.Reverse();
                        _args.Feedback.TaskStatus = TASK_RUN_STATUS.NAVIGATING;
                        dispatcherModule.TaskFeedback(_args.Feedback); //回報任務狀態
                        _ = Task.Run(() => ReportTaskStateToEQSimulator(_args.action, _args.nextMoveTrajectory.First().Point_ID.ToString()));
                        await BarcodeMove(_args, _token);
                    }

                    void _CargoStateSimulate(ACTION_TYPE action, string cstID)
                    {
                        if (action == ACTION_TYPE.Load || action == ACTION_TYPE.LoadAndPark)
                        {
                            runningSTatus.CSTID = new string[0];
                            runningSTatus.Cargo_Status = 0;
                        }
                        else if (action == ACTION_TYPE.Unload)
                        {
                            runningSTatus.CSTID = new string[] { cstID };
                            runningSTatus.Cargo_Status = 1;
                        }
                    }
                }, token);

            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("前一任務已中斷");
            }

            return new TaskDownloadRequestResponse() { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.OK };
        }

        private async Task BarcodeMove(BarcodeMoveArguments moveArgs, CancellationToken token)
        {

            int currentTag = runningSTatus.Last_Visited_Node;
            int currentTagIndex = moveArgs.nextMoveTrajectory.GetTagList().ToList().IndexOf(currentTag);
            moveArgs.nextMoveTrajectory = moveArgs.orderTrajectory.Skip(currentTagIndex).ToArray();

            clsMapPoint[] Trajectory = moveArgs.nextMoveTrajectory.ToArray();
            ACTION_TYPE action = moveArgs.action;

            var taskFeedbackData = moveArgs.Feedback;
            double rotateSpeed = 10;
            double moveSpeed = 10;
            int idx = 0;
            //轉向第一個點
            if (moveArgs.action == ACTION_TYPE.None)
            {
                TurnToNextPoint(Trajectory, rotateSpeed, moveSpeed);
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

                await MoveChangeSimulation(currentX, currentY, station.X, station.Y, speed: parameters.MoveSpeedRatio * parameters.SpeedUpRate, token);
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
                        SimulationThetaChange(runningSTatus.Coordination.Theta, targetAngle, token);
                        runningSTatus.Coordination.Theta = targetAngle;
                    }
                }
                taskFeedbackData.PointIndex = moveArgs.orderTrajectory.GetTagList().ToList().IndexOf(stationTag);
                taskFeedbackData.TaskStatus = TASK_RUN_STATUS.NAVIGATING;

                int feedBackCode = dispatcherModule.TaskFeedback(taskFeedbackData).Result; //回報任務狀態

                idx += 1;
            }
            if (action == ACTION_TYPE.None && Trajectory.Length > 0)
                SimulationThetaChange(runningSTatus.Coordination.Theta, Trajectory.Last().Theta, token);

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

        }
    }
}

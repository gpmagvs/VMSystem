using AGVSystemCommonNet6;
using AGVSystemCommonNet6.TASK;
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

namespace VMSystem.AGV
{
    public class clsAGVSimulation
    {
        private IAGV agv => dispatcherModule.agv;
        private double[] batteryLevelSim = new double[] { 100.0, 100.0 };
        private readonly clsAGVTaskDisaptchModule dispatcherModule;
        private AGVStatusDBHelper agvStateDbHelper = new AGVStatusDBHelper();
        private List<clsAGVTrafficState> TrafficState => TrafficControlCenter.DynamicTrafficState.AGVTrafficStates.Values.ToList().FindAll(_agv => _agv.AGVName != agv.Name);
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
                        batteryLevelSim = new double[] { agv_data.BatteryLevel_1, agv_data.BatteryLevel_2 };
                        agv.states.Last_Visited_Node = int.Parse(agv_data.CurrentLocation);
                        agv.states = agv.states;
                    }
                }
                BatterSimulation();
            }
        }
        public async Task<TaskDownloadRequestResponse> ActionRequestHandler(clsTaskDownloadData data)
        {
            agv.states.AGV_Status = clsEnums.MAIN_STATUS.RUN;
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
                    NewMethod(action, ExecutingTrajecory, data.Trajectory, stateDto, moveCancelTokenSource.Token);
                    if (action == ACTION_TYPE.Load | action == ACTION_TYPE.Unload)
                    {
                        //模擬LDULD
                        Thread.Sleep(1000);
                        if (action == ACTION_TYPE.Load)
                        {
                            agv.states.CSTID = new string[0];
                            agv.states.Cargo_Status = 0;
                        }
                        else
                        {
                            agv.states.CSTID = data.CST.Select(cst => cst.CST_ID).ToArray();
                            agv.states.Cargo_Status = 1;
                        }
                        ReportTaskStateToEQSimulator(action, ExecutingTrajecory.Last().Point_ID.ToString());
                        NewMethod(action, ExecutingTrajecory.Reverse().ToArray(), data.Trajectory, stateDto, moveCancelTokenSource.Token);
                    }
                    double finalTheta = ExecutingTrajecory.Last().Theta;
                    SimulationThetaChange(agv.states.Coordination.Theta, finalTheta);
                    agv.states.Coordination.Theta = finalTheta;
                    Thread.Sleep(1000);

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

        private void ReportTaskStateToEQSimulator(ACTION_TYPE ActionType,string EQName)
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

        private void NewMethod(ACTION_TYPE action, clsMapPoint[] Trajectory, clsMapPoint[] OriginTrajectory, FeedbackData stateDto, CancellationToken cancelToken)
        {
            double rotateSpeed = 10;
            double moveSpeed = 10;
            int idx = 0;
            // 移动 AGV
            foreach (clsMapPoint station in Trajectory)
            {
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
                int currentTag = agv.states.Last_Visited_Node;
                double currentX = agv.states.Coordination.X;
                double currentY = agv.states.Coordination.Y;
                double currentAngle = agv.states.Coordination.Theta;
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
                    SimulationThetaChange(agv.states.Coordination.Theta, targetAngle);
                    agv.states.Coordination.Theta = targetAngle;
                }
                MoveChangeSimulation(currentX, currentY, station.X, station.Y);
                //Thread.Sleep(1000);
                agv.states.Coordination.X = station.X;
                agv.states.Coordination.Y = station.Y;

                var pt = StaMap.GetPointByTagNumber(agv.states.Last_Visited_Node);
                string err_msg = "";
                Task.Factory.StartNew(() => StaMap.UnRegistPoint(agv.Name, pt, out err_msg ));
                
                agv.states.Last_Visited_Node = stationTag;
                StaMap.RegistPoint(agv.Name, StaMap.GetPointByTagNumber(stationTag), out err_msg);

                stateDto.PointIndex =OriginTrajectory.ToList().IndexOf(station);
                //stateDto.PointIndex = idx;
                stateDto.TaskStatus = TASK_RUN_STATUS.NAVIGATING;
                int feedBackCode = dispatcherModule.TaskFeedback(stateDto).Result; //回報任務狀態

                if (feedBackCode != 0)
                {
                    if (feedBackCode == 1) //停等訊號
                    {

                    }
                }
                agv.states = agv.states;
            idx += 1;
            }
        }

        private void MoveChangeSimulation(double CurrentX,double CurrentY,double TargetX,double TargetY)
        {
            double O_Distance_X = TargetX - CurrentX;
            double O_Distance_Y = TargetY - CurrentY;
            double O_Distance_All = Math.Pow(Math.Pow(O_Distance_X, 2) + Math.Pow(O_Distance_Y, 2), 0.5); //用來計算總共幾秒
            double speed = 1;
            double TotalSpendTime = Math.Ceiling(O_Distance_All / speed);
            double MoveSpeed_X = O_Distance_X / TotalSpendTime;
            double MoveSpeed_Y = O_Distance_Y / TotalSpendTime;

            for (int i = 0; i < TotalSpendTime; i++)
            {
                agv.states.Coordination.X = CurrentX +i * MoveSpeed_X; 
                agv.states.Coordination.Y = CurrentY + i * MoveSpeed_Y;
                Thread.Sleep(100);
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
                    agv.states.Electric_Volume = batteryLevelSim;
                    _ = Task.Factory.StartNew(async () =>
                    {
                        await agvStateDbHelper.UpdateBatteryLevel(agv.Name, batteryLevelSim);
                    });
                    await Task.Delay(1000);
                }
            });
        }
    }
}

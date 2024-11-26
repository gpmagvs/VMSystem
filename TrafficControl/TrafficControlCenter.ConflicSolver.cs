using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using Microsoft.EntityFrameworkCore.Metadata;
using RosSharp.RosBridgeClient;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using VMSystem.ViewModels;
using VMSystem.VMS;

namespace VMSystem.TrafficControl
{

    public partial class TrafficControlCenter
    {


        public class PathConflicRequest
        {

            public enum CONFLIC_STATE
            {
                /// <summary>
                /// 剩餘路徑與其他車輛有干涉
                /// </summary>
                REMAIN_PATH_COLLUSION_CONFLIC,
                /// <summary>
                /// 位於窄道但其他車輛朝向非水平
                /// </summary>
                NARROW_PATH_CONFLIC
            }

            public readonly IAGV RaisedRequestAGV;

            public readonly IEnumerable<IAGV> ConflicToAGVCollection;

            public IEnumerable<MapPoint> TrajectPlan { get; }

            public readonly CONFLIC_STATE ConflicState;

            public PathConflicRequest(IAGV raisedRequestAGV,
                IEnumerable<IAGV> conflicToAGVCollection,
                IEnumerable<MapPoint> trajectPlan,
                CONFLIC_STATE conflicState)
            {
                RaisedRequestAGV = raisedRequestAGV;
                ConflicToAGVCollection = conflicToAGVCollection;
                TrajectPlan = trajectPlan;
                ConflicState = conflicState;
            }
        }

        private static bool IsConflicSolving = false;
        private static SemaphoreSlim _solveRequestSemaphore = new SemaphoreSlim(1, 1);
        private static async void HandleOnPathConflicForSoloveRequest(object? sender, PathConflicRequest requestDto)
        {
            await _solveRequestSemaphore.WaitAsync();
            try
            {
                if (IsConflicSolving)
                {
                    logger.Trace($"{requestDto.RaisedRequestAGV.Name} Raise Path Conflic Request but traffic solver is running.");
                    return;
                }
                IsConflicSolving = true;
                List<IAGV> vehicles = new List<IAGV>() { requestDto.RaisedRequestAGV };
                vehicles.AddRange(requestDto.ConflicToAGVCollection);
                PathConflicSolver solver = new PathConflicSolver(vehicles);
                solver.OnSloveDone += OnSloveDone;
                solver.StartSolve();
                logger.Trace($"Path Conflic Solve Start ! ");
            }
            catch (Exception ex)
            {
                IsConflicSolving = false;
                throw ex;
            }
            finally { _solveRequestSemaphore.Release(); }
        }

        private static void OnSloveDone(object? sender, PathConflicSolver.PathConflicSolveResult result)
        {
            IsConflicSolving = false;
            logger.Trace($"Path Conflic Solve Done ! {result.ToJson()}");
        }

        public class PathConflicSolver
        {

            public class PathConflicSolveResult
            {
                public readonly bool Success;
                public readonly string Message;
                public PathConflicSolveResult(bool success, string message)
                {
                    this.Success = success;
                    this.Message = message;
                }
            }

            public event EventHandler<PathConflicSolveResult> OnSloveDone;
            public readonly IEnumerable<IAGV> Vehicles;
            public PathConflicSolver(IEnumerable<IAGV> Vehicles)
            {
                this.Vehicles = Vehicles;
            }

            public async Task StartSolve()
            {
                PathConflicSolveResult result = new PathConflicSolveResult(true, "");
                try
                {
                    bool allAgvOnlining = await OnlineRequest();
                    if (!allAgvOnlining)
                        result = new PathConflicSolveResult(false, "尚有衝突車輛仍離線");
                    bool allAgvTaskCancelAckAccept = await CancelVehiclesTask();
                    if (!allAgvTaskCancelAckAccept)
                        result = new PathConflicSolveResult(false, "尚有衝突車輛拒絕任務取消");
                    bool allAgvIsIdling = await WaitAllAGVIdle();
                    if (!allAgvIsIdling)
                        result = new PathConflicSolveResult(false, "尚有衝突車輛非IDLE狀態(未Cycle Stop當前任務)");

                    var OrderedVehicles = OrderVehicleByOrderAction();

                    List<MapPoint> PointsUsed = new List<MapPoint>();
                    var toVoidVehicle = OrderedVehicles.Reverse().First();

                    bool isToAvoidVehicleIsExecutingOrder = toVoidVehicle.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;

                    TaskBase runningTaskObj = toVoidVehicle.CurrentRunningTask();
                    var currentPathSearch = runningTaskObj.RealTimeOptimizePathSearchReuslt;
                    var avoidPoints = StaMap.GetAvoidStations();
                    var usableAvoidPoints = avoidPoints.Where(pt => !PointsUsed.Contains(pt))
                                                        .OrderBy(pt => pt.CalculateDistance(toVoidVehicle.currentMapPoint));
                    clsTaskDownloadData taskDto = null;
                    if (usableAvoidPoints.Any())
                    {
                        var avoidPoint = usableAvoidPoints.Where(pt => !StaMap.RegistDictionary.ContainsKey(pt.TagNumber))
                                                          .FirstOrDefault(pt => pt.TagNumber != toVoidVehicle.currentMapPoint.TagNumber);
                        if (avoidPoint == null)
                        {
                            result = new PathConflicSolveResult(false, "No avoid point can use...");
                            return;
                        }

                        using (var agvsDb = new AGVSDatabase())
                        {
                            agvsDb.tables.Tasks.Add(new clsTaskDto
                            {
                                Action = ACTION_TYPE.None,
                                DesignatedAGVName = toVoidVehicle.Name,
                                TaskName = $"TAF-{DateTime.Now.ToString("yyMMddHHmmssfff")}",
                                To_Station = avoidPoint.TagNumber + "",
                                Priority = 10
                            });
                            await agvsDb.SaveChanges();
                        }
                        if (isToAvoidVehicleIsExecutingOrder) //需要備感車的車輛本來就在執行任務
                        {
                            //把原任務狀態設定等待
                            runningTaskObj.OrderData.State = TASK_RUN_STATUS.WAIT;
                            runningTaskObj.OrderData.Priority = 9;
                            VMSManager.HandleTaskDBChangeRequestRaising(this, runningTaskObj.OrderData);
                        }

                        while (toVoidVehicle.states.Last_Visited_Node != avoidPoint.TagNumber)
                        {
                            await Task.Delay(1000);

                        }
                        var otherAGV = OrderedVehicles.FilterOutAGVFromCollection(toVoidVehicle);
                        foreach (var agv in otherAGV)
                        {
                            if (agv.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                            {
                                agv.CurrentRunningTask().DistpatchToAGV();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result = new PathConflicSolveResult(false, ex.Message);
                }

                finally
                {
                    OnSloveDone?.Invoke(this, result);
                }
            }

            private IEnumerable<IAGV> OrderVehicleByOrderAction()
            {
                return Vehicles.OrderBy(vehicle => WeightsCalculate(vehicle));

                int WeightsCalculate(IAGV vehicle)
                {
                    int _weight = 1;

                    bool _isAGVExecutingTask = vehicle.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;

                    if (_isAGVExecutingTask)
                    {
                        _weight = 100;
                        var currentOrderAction = vehicle.taskDispatchModule.OrderHandler.OrderData.Action;
                        switch (currentOrderAction)
                        {
                            case AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Carry:

                                var stage = vehicle.taskDispatchModule.OrderHandler.RunningTask.Stage;

                                if (stage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.Traveling_To_Destine)
                                    _weight = 500;
                                else if (stage == AGVSystemCommonNet6.AGVDispatch.VehicleMovementStage.Traveling_To_Source)
                                    _weight = 400;
                                else
                                    _weight = 390;
                                break;

                            case AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Load:
                                _weight = 180;
                                break;

                            case AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Unload:
                                _weight = 190;
                                break;

                            case AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None:
                                _weight = 190;
                                break;

                            case AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Charge:
                                _weight = 10;
                                break;
                        }
                    }
                    else
                    {

                        _weight = 1;
                    }


                    return 0;
                }
            }

            private async Task<bool> WaitAllAGVIdle()
            {

                var results = await Task.WhenAll(Vehicles.Select(vehicle => WaitIdle(vehicle)).ToArray());
                return results.All(success => success);

                async Task<bool> WaitIdle(IAGV vehicle)
                {
                    CancellationTokenSource _waitCancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    while (vehicle.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.IDLE)
                    {
                        await Task.Delay(1000);
                        if (_waitCancel.IsCancellationRequested)
                            return false;
                    }
                    return true;
                }
            }

            private async Task<bool> OnlineRequest()
            {
                var offlineVehicles = this.Vehicles.Where(agv => agv.online_state == AGVSystemCommonNet6.clsEnums.ONLINE_STATE.OFFLINE);
                var offlineTasks = offlineVehicles.Select(vehicle => OnlineAGV(vehicle)).ToArray();
                async Task<bool> OnlineAGV(IAGV vehicle)
                {
                    return vehicle.AGVOnlineFromAGVS(out var mesg);
                }
                var results = await Task.WhenAll(offlineTasks);
                return results.All(success => success);
            }

            public async Task<bool> CancelVehiclesTask()
            {
                List<Task> cancelRequestTasks = new List<Task>();
                var results = await Task.WhenAll(Vehicles.Select(vehicle => CancelRequest(vehicle)).ToArray());
                Log($"All vehicle task canceled done.");
                return results.All(success => success);

                async Task<bool> CancelRequest(IAGV _vehicle)
                {
                    var dispatchModule = _vehicle.taskDispatchModule;

                    if (dispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                        return true;

                    var runningHandler = dispatchModule.OrderHandler;
                    var runningTask = runningHandler.RunningTask;
                    runningTask.CancelTask();
                    return true;
                }

            }

            private void Log(string message)
            {
                logger.Trace($"[PathConflicSolver]-{message}");
            }
        }

    }
}

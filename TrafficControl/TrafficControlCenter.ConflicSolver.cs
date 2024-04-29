using AGVSystemCommonNet6.Log;
using VMSystem.AGV;
using VMSystem.ViewModels;

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

            public readonly CONFLIC_STATE ConflicState;

            public PathConflicRequest(IAGV raisedRequestAGV, IEnumerable<IAGV> conflicToAGVCollection, CONFLIC_STATE conflicState)
            {
                RaisedRequestAGV = raisedRequestAGV;
                ConflicToAGVCollection = conflicToAGVCollection;
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
                    LOG.TRACE($"{requestDto.RaisedRequestAGV.Name} Raise Path Conflic Request but traffic solver is running.");
                    return;
                }
                IsConflicSolving = true;
                List<IAGV> vehicles = new List<IAGV>() { requestDto.RaisedRequestAGV };
                vehicles.AddRange(requestDto.ConflicToAGVCollection);
                PathConflicSolver solver = new PathConflicSolver(vehicles);
                solver.OnSloveDone += OnSloveDone;
                solver.StartSolve();

                LOG.WARN($"Path Conflic Solve Start ! ");
            }
            catch (Exception)
            {

                throw;
            }
            finally { _solveRequestSemaphore.Release(); }
        }

        private static void OnSloveDone(object? sender, EventArgs e)
        {
            IsConflicSolving = false;
            LOG.INFO($"Path Conflic Solve Done ! ");
        }

        public class PathConflicSolver
        {
            public event EventHandler OnSloveDone;
            public readonly IEnumerable<IAGV> Vehicles;
            public PathConflicSolver(IEnumerable<IAGV> Vehicles)
            {
                this.Vehicles = Vehicles;
            }

            public async Task StartSolve()
            {
                await CancelVehiclesTask();
                OnSloveDone?.Invoke(this, EventArgs.Empty);
            }

            public async Task CancelVehiclesTask()
            {
                List<Task> cancelRequestTasks = new List<Task>();

                foreach (IAGV vehicle in Vehicles)
                {
                    cancelRequestTasks.Add(CancelRequest(vehicle));
                }
                Task.WaitAll(cancelRequestTasks.ToArray());

                async Task CancelRequest(IAGV _vehicle)
                {
                    var dispatchModule = _vehicle.taskDispatchModule;
                    var runningHandler = dispatchModule.OrderHandler;
                    var runningTask = runningHandler.RunningTask;
                    await runningTask.SendCancelRequestToAGV();
                }

            }

        }

    }
}

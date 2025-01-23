using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.MAP;
using Microsoft.Data.SqlClient.Server;
using Newtonsoft.Json;
using System.Diagnostics;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using VMSystem.VMS;
using static AGVSystemCommonNet6.DATABASE.DatabaseCaches;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.TrafficControl.Solvers
{
    /// <summary>
    /// 處理趕車
    /// </summary>
    public class AvoidVehicleSolver
    {
        public IAGV Vehicle { get; }
        public IAGV RaiseAvoidVehicle { get; }
        public ACTION_TYPE Action { get; }

        public clsTaskDto Order { get; private set; }

        private AGVSDbContext agvsDb { get; }

        private static SemaphoreSlim _CreateOrderSemaphore = new SemaphoreSlim(1, 1);

        public AvoidVehicleSolver(IAGV Vehicle, IAGV RaiseAvoidVehicle, ACTION_TYPE Action, AGVSDbContext agvsDb)
        {
            this.Vehicle = Vehicle;
            this.RaiseAvoidVehicle = RaiseAvoidVehicle;
            this.Action = Action;
            this.agvsDb = agvsDb;

        }

        public async Task<ALARMS> Solve()
        {
            try
            {
                if (Vehicle.online_state == AGVSystemCommonNet6.clsEnums.ONLINE_STATE.OFFLINE)
                {
                    _Log($"{RaiseAvoidVehicle.Name} Request {Vehicle.Name} Leave from but online state of {Vehicle.Name} is OFFLine. return ALARMS.TrafficDriveVehicleAwaybutVehicleNotOnline");
                    return ALARMS.TrafficDriveVehicleAwaybutVehicleNotOnline;
                }
                bool _IsVehicleWhereShouldLeaveIsExecutingOrderNow = IsVehicleWhereShouldLeaveIsExecutingOrderNow();

                if (!_IsVehicleWhereShouldLeaveIsExecutingOrderNow)
                {
                    _Log($"{Vehicle.Name} NOT Executing Order,Add a avoid command order");
                    try
                    {
                        await TryAddOrderToDatabase();
                        _Log($"Order add to {Vehicle.Name} Success! Order info: \r\n{JsonConvert.SerializeObject(this.Order, Formatting.Indented)}");
                    }
                    catch (Exception ex)
                    {
                        _Log($"Order add to {Vehicle.Name} Failure! Return ALARMS.TrafficDriveVehicleAwaybutAppendOrderToDatabaseFail. {ex.Message},{ex.StackTrace}");
                        return ALARMS.TrafficDriveVehicleAwaybutAppendOrderToDatabaseFail;
                    }
                }
                RaiseAvoidVehicle.CurrentRunningTask()?.UpdateStateDisplayMessage($"Waiting [{Vehicle.Name}] Leave Pt:{Vehicle.currentMapPoint.Graph.Display}", true);
                await WaitVehicleLeave();
                _Log($"Now is clear Start Order");
                return ALARMS.NONE;
            }
            catch (VMSException ex)
            {
                _Log(ex.Message + ex.StackTrace);
                return ex.Alarm_Code;
            }
            catch (OperationCanceledException ex)
            {
                _Log(ex.Message + ex.StackTrace);
                return ALARMS.TrafficDriveVehicleAwaybutAppendOrderToDatabaseFail;
            }
            catch (Exception ex)
            {
                _Log(ex.Message + ex.StackTrace);
                return ALARMS.TrafficDriveVehicleAwaybutWaitOtherVehicleReleasePointTimeout;
            }
        }

        /// <summary>
        /// 檢查被趕車是否有任務正在執行
        /// </summary>
        /// <returns></returns>
        private bool IsVehicleWhereShouldLeaveIsExecutingOrderNow()
        {
            if (Vehicle.online_state == AGVSystemCommonNet6.clsEnums.ONLINE_STATE.OFFLINE)
                return false;
            return Vehicle.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
        }
        private async Task TryAddOrderToDatabase()
        {
            try
            {
                await _CreateOrderSemaphore.WaitAsync();
                MapPoint? destinePt = null;
                destinePt = this.Action == ACTION_TYPE.Park ? _GetParkPort() : _GetNormalPoint();

                if (destinePt == null)
                    throw new VMSException(ALARMS.TrafficDriveVehicleAwayButCannotFindAvoidPosition);

                clsTaskDto order = new clsTaskDto();
                order.RecieveTime = DateTime.Now;
                order.TaskName = $"Avoid-{this.Action}-{DateTime.Now.ToString("yyyyMMdd_HHmmssfff")}";
                order.DesignatedAGVName = Vehicle.Name;
                order.Action = destinePt.IsChargeAble() ? ACTION_TYPE.Charge : Action;
                order.State = TASK_RUN_STATUS.WAIT;
                order.To_Station = destinePt.TagNumber.ToString();
                order.To_Slot = "0";
                await Task.Delay(100);
                CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    try
                    {
                        if (cancellation.IsCancellationRequested)
                            throw new VMSException() { Alarm_Code = ALARMS.TrafficDriveVehicleAwaybutAppendOrderToDatabaseFail };

                        this.agvsDb.Tasks.Add(order);
                        int changed = await this.agvsDb.SaveChangesAsync();

                        if (changed >= 1)
                        {
                            await WaitTaskCreatedAsync(order);
                        }

                        this.Order = order;
                        return;
                    }
                    catch (VMSException ex)
                    {
                        throw ex;
                    }
                    catch (Exception ex)
                    {
                        await Task.Delay(1000);
                        Console.WriteLine(ex.Message);
                        continue;

                    }
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                _CreateOrderSemaphore.Release();
            }
        }

        private async Task WaitTaskCreatedAsync(clsTaskDto order)
        {
            while (DatabaseCaches.TaskCaches.InCompletedTasks.FirstOrDefault(tk => tk.TaskName == order.TaskName) == null)
            {
                await Task.Delay(100);
            }
        }

        private async Task WaitVehicleLeave()
        {
            int tagOfVehicleBegin = Vehicle.currentMapPoint.TagNumber; //AGV起始位置(在 port裡面)
            bool _isVehicleAtPortBegin = Vehicle.currentMapPoint.StationType != MapPoint.STATION_TYPE.Normal;
            int tagOfEntryPointOfCurrentPort = _isVehicleAtPortBegin ? Vehicle.currentMapPoint.TargetNormalPoints().First().TagNumber : -1; //AGV所在位置的進入點
            CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Debugger.IsAttached ? 30 : 300));

            //等待已不在Port裡面而
            while (IsAGVStillAtPort(tagOfVehicleBegin))
            {
                try
                {
                    await Task.Delay(1000, cancellation.Token);

                    if (Vehicle.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN || Vehicle.online_state != AGVSystemCommonNet6.clsEnums.ONLINE_STATE.ONLINE)
                    {
                        throw new VMSException() { Alarm_Code = ALARMS.TrafficDriveVehicleAwaybutVehicleCannotLeaveNow };
                    }

                }
                catch (TaskCanceledException ex)
                {
                    _Log($"Wait {Vehicle.Name} leave Tag {tagOfVehicleBegin} Timeout");
                    throw new VMSException() { Alarm_Code = ALARMS.TrafficDriveVehicleAwaybutWaitOtherVehicleReleasePointTimeout };
                }
            }

            bool IsAGVStillAtPort(int tagOfVehicleBegin)
            {
                return Vehicle.currentMapPoint.TagNumber == tagOfVehicleBegin;
            }
        }

        private MapPoint? _GetParkPort()
        {
            List<int> assignToOrderTags = new List<int>();

            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.InCompletedTasks.Select(tk => tk.From_Station_Tag).ToList());
            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.InCompletedTasks.Select(tk => tk.To_Station_Tag).ToList());
            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.RunningTasks.Select(tk => tk.From_Station_Tag).ToList());
            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.RunningTasks.Select(tk => tk.To_Station_Tag).ToList());

            var allParkablePoints = StaMap.Map.Points.Values.Where(pt => pt.StationType != MapPoint.STATION_TYPE.Normal)
                                                            .Where(pt => pt.TagNumber != Vehicle.currentMapPoint.TagNumber)
                                                            .Where(pt => pt.IsAvoid || pt.IsChargeAble()) //可停車點
                                                            .Where(pt => !VMSManager.AllAGV.Select(v => v.currentMapPoint.TagNumber).Contains(pt.TagNumber)) //沒有車子停著的點
                                                            .Where(pt => !assignToOrderTags.Contains(pt.TagNumber)); //沒有被指派任務起終點的點
            if (!allParkablePoints.Any())
                return null;

            Dictionary<MapPoint, double> distanceMapToParkPts = allParkablePoints.ToDictionary(pt => pt, pt => _GetDistanceToParkPoint(pt));
            var ordered = distanceMapToParkPts.OrderBy(kp => kp.Value);
            return ordered.First().Key;

            double _GetDistanceToParkPoint(MapPoint parkPoint)
            {
                PathFinder _finder = new PathFinder();
                clsPathInfo _result = _finder.FindShortestPath(Vehicle.currentMapPoint.TagNumber, parkPoint.TagNumber, new PathFinder.PathFinderOption
                {
                    Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
                    OnlyNormalPoint = false,
                    Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE
                });
                return _result.total_travel_distance;
            }
        }

        private MapPoint? _GetNormalPoint()
        {
            return null;
        }
        private void _Log(string message)
        {
            //用AGV Log實例
            RaiseAvoidVehicle?.logger.Trace("[AvoidVehicleSolver]" + message);
        }
    }
}

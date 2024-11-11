using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.MAP;
using System.Diagnostics;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.Solvers
{
    /// <summary>
    /// 處理趕車
    /// </summary>
    public class AvoidVehicleSolver
    {
        public IAGV Vehicle { get; }
        public ACTION_TYPE Action { get; }

        public clsTaskDto Order { get; private set; }

        private AGVSDbContext agvsDb { get; }

        private static SemaphoreSlim _CreateOrderSemaphore = new SemaphoreSlim(1, 1);

        public AvoidVehicleSolver(IAGV Vehicle, ACTION_TYPE Action, AGVSDbContext agvsDb)
        {
            this.Vehicle = Vehicle;
            this.Action = Action;
            this.agvsDb = agvsDb;

        }

        public async Task<ALARMS> Solve()
        {
            try
            {
                if (Vehicle.online_state == AGVSystemCommonNet6.clsEnums.ONLINE_STATE.OFFLINE)
                    return ALARMS.TrafficDriveVehicleAwaybutVehicleNotOnline;

                if (Vehicle.main_state != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN)
                    await TryAddOrderToDatabase();

                await WaitVehicleLeave();
                return ALARMS.NONE;
            }
            catch (VMSException ex)
            {
                return ex.Alarm_Code;
            }
            catch (OperationCanceledException ex)
            {
                return ALARMS.TrafficDriveVehicleAwaybutAppendOrderToDatabaseFail;
            }
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
                order.Action = Action;
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
            int tagOfVehicleBegin = Vehicle.currentMapPoint.TagNumber;
            CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Debugger.IsAttached ? 30 : 300));
            while (Vehicle.currentMapPoint.TagNumber == tagOfVehicleBegin)
            {
                try
                {
                    await Task.Delay(1000, cancellation.Token);
                }
                catch (TaskCanceledException ex)
                {
                    throw new VMSException() { Alarm_Code = ALARMS.TrafficDriveVehicleAwaybutWaitOtherVehicleReleasePointTimeout };
                }
            }
        }

        private MapPoint? _GetParkPort()
        {
            List<int> assignToOrderTags = new List<int>();

            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.InCompletedTasks.Select(tk => tk.From_Station_Tag).ToList());
            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.InCompletedTasks.Select(tk => tk.To_Station_Tag).ToList());
            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.RunningTasks.Select(tk => tk.From_Station_Tag).ToList());
            assignToOrderTags.AddRange(DatabaseCaches.TaskCaches.RunningTasks.Select(tk => tk.To_Station_Tag).ToList());

            var allParkablePoints = StaMap.Map.Points.Values.Where(pt => pt.TagNumber != Vehicle.currentMapPoint.TagNumber)
                                                            .Where(pt => pt.IsParking) //可停車點
                                                            .Where(pt => !VMSManager.AllAGV.Select(v => v.currentMapPoint.TagNumber).Contains(pt.TagNumber)) //沒有車子停著的點
                                                            .Where(pt => !assignToOrderTags.Contains(pt.TagNumber)); //沒有被指派任務起終點的點
            if (!allParkablePoints.Any())
                return null;

            return allParkablePoints.First(); //test
        }

        private MapPoint? _GetNormalPoint()
        {
            return null;
        }

    }
}

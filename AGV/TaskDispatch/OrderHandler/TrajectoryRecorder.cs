using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Notify;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class TrajectoryRecorder
    {
        private IAGV agv;
        private clsTaskDto orderData;
        private CancellationTokenSource TrajectoryRecordCancelTokenSource;
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        public TrajectoryRecorder(IAGV agv, clsTaskDto orderData)
        {
            this.agv = agv;
            this.orderData = orderData;
        }

        /// <summary>
        /// 開始記錄軌跡
        /// </summary>
        /// <param name="recordInterval">Unit:秒</param>
        /// <returns></returns>
        public Task Start(double recordInterval = 1)
        {
            return Task.Run(async () =>
            {
                List<clsTrajCoordination> _TrajectoryTempStorage = new List<clsTrajCoordination>();
                TrajectoryRecordCancelTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(10));//設個上限10min 才不會在沒有呼叫Stop的情況下無限迴圈

                try
                {

                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(recordInterval));
                        if (TrajectoryRecordCancelTokenSource.IsCancellationRequested)
                            break;

                        double x = agv.states.Coordination.X;
                        double y = agv.states.Coordination.Y;
                        double theta = agv.states.Coordination.Theta;

                        if (!_IsCoordinationChanged(x, y))
                            continue;

                        DateTime time = DateTime.Now;
                        _TrajectoryTempStorage.Add(new clsTrajCoordination() { Time = time, X = x, Y = y, Theta = theta });

                        bool _IsCoordinationChanged(double currentX, double currentY)
                        {
                            if (!_TrajectoryTempStorage.Any())
                                return true;
                            var lastRecord = _TrajectoryTempStorage.Last();
                            return lastRecord.X != currentX || lastRecord.Y != currentY;
                        }

                    }
                }
                catch (Exception ex)
                {

                }
                finally
                {
                    await SaveTrajectoryToDatabase(_TrajectoryTempStorage);
                    _TrajectoryTempStorage.Clear();
                    _TrajectoryTempStorage = null;
                }


            });
        }

        /// <summary>
        /// 停止記錄軌跡
        /// </summary>
        public void Stop()
        {
            TrajectoryRecordCancelTokenSource?.Cancel();
        }
        async Task SaveTrajectoryToDatabase(List<clsTrajCoordination> _TrajectoryTempStorage)
        {
            string taskID = orderData.TaskName;
            string agvName = agv.Name;
            TrajectoryDBStoreHelper helper = new TrajectoryDBStoreHelper();
            var result = await helper.StoreTrajectory(taskID, agvName, _TrajectoryTempStorage.ToJson(Newtonsoft.Json.Formatting.None));
            if (!result.success)
            {
                NotifyServiceHelper.SUCCESS($"任務-{orderData.TaskName}軌跡數據儲存至資料庫失敗:{result.error_msg}");
                logger.Error($"[{agv.Name}] trajectory store of task {taskID} DB ERROR : {result.error_msg}");
            }
            else
                NotifyServiceHelper.SUCCESS($"任務-{orderData.TaskName}軌跡數據已儲存至資料庫");
        }
    }
}

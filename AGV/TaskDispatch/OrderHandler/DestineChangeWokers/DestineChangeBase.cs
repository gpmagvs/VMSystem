using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using System.Diagnostics;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers
{
    /// <summary>
    /// 這是一個用來變換目的地的類別
    /// </summary>
    public abstract class DestineChangeBase : VehicleOrderController
    {
        public readonly IAGV agv;
        public readonly clsTaskDto order;
        public event EventHandler<int> OnStartChanged;
        /// <summary>
        /// 目標站點Tag
        /// </summary>
        public int destineTag => order.To_Station_Tag;
        /// <summary>
        /// 目標站點MapPoint
        /// </summary>
        public MapPoint destineMapPoint => StaMap.GetPointByTagNumber(destineTag);

        /// <summary>
        /// 除了監控車之外的所有車輛
        /// </summary>
        public List<IAGV> othersVehicles => VMSManager.AllAGV.FilterOutAGVFromCollection(agv).ToList();


        /// <summary>
        /// 
        /// </summary>
        /// <param name="agv">車輛</param>
        /// <param name="order">任務訂單</param>
        public DestineChangeBase(IAGV agv, clsTaskDto order, SemaphoreSlim taskTableLocker) : base(taskTableLocker)
        {
            this.agv = agv;
            this.order = order;
        }


        internal void ReStart()
        {
            IsCycleStopRaised = false;
            _RestartFlag = true;
            CancelTaskMRE.Set();
        }
        internal async Task StartMonitorAsync()
        {
            await Task.Delay(1).ContinueWith(async t =>
            {
                try
                {
                    while (!IsCurrentSubTaskFinish())
                    {
                        await Task.Delay(100);
                        if (IsNeedChange())
                        {
                            if (agv.NavigationState.IsWaitingConflicSolve)
                            {
                                CancelTaskMRE.Reset();
                                CycleStopProgressRunMRE.Reset();
                                IsCycleStopRaised = true;
                                if (!CancelTaskMRE.WaitOne(TimeSpan.FromSeconds(10)))
                                    break;
                            }
                            if (_RestartFlag)
                                continue;

                            OnStartChangedInovke();
                            (bool confirm, string message) = await CancelOrderAndWaitVehicleIdle(agv, order, "Change Charge Station", 60);
                            if (!confirm)
                            {
                                throw new TimeoutException(message);
                            }
                            //await Task.Delay(Debugger.IsAttached ? 3000 : 1);
                            CycleStopProgressRunMRE.Set();
                            await WaitOrderNotRun(order);
                            var newOrder = order.Clone();
                            newOrder.TaskName = order.TaskName + "_Chg";
                            newOrder.RecieveTime = DateTime.Now;
                            newOrder.FinishTime = DateTime.MinValue;
                            newOrder.State = AGVSystemCommonNet6.AGVDispatch.Messages.TASK_RUN_STATUS.WAIT;
                            newOrder.Priority = 12300;
                            newOrder.FailureReason = "";
                            newOrder.To_Station = GetNewDestineTag() + "";
                            newOrder.DispatcherName = "";
                            bool orderModifySuccess = await AddNewOrder(newOrder);
                            if (orderModifySuccess)
                            {
                                break;
                            }
                        }

                    }
                }
                catch (Exception ex)
                {
                    agv.logger.Error("[DestineChangeBase]-" + ex.Message + ex.StackTrace);
                }
                finally
                {
                    agv.logger.Info($"[DestineChangeBase]-Finish Monitor");
                }
            });
        }

        protected override bool IsVehicleNotIDLE(IAGV agv)
        {
            return agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.RUN;
        }

        private void OnStartChangedInovke()
        {
            OnStartChanged?.Invoke(this, this.destineTag);
        }

        protected abstract int GetNewDestineTag();
        internal abstract bool IsNeedChange();

        protected virtual bool IsCurrentSubTaskFinish()
        {
            if (agv.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN)
                return true;

            if (agv.taskDispatchModule.OrderExecuteState != clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                return true;
            TaskBase? currentRunTask = agv.CurrentRunningTask();
            if (currentRunTask == null)
                return true;

            if (currentRunTask.Stage != VehicleMovementStage.Traveling_To_Destine)
                return true;

            return false;
        }

    }
}

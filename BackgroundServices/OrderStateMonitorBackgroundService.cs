using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using NLog;
using System.Collections.Concurrent;
using VMSystem.VMS;

namespace VMSystem.BackgroundServices
{
    public class OrderStateMonitorBackgroundService : BackgroundService
    {
        private ConcurrentDictionary<string, clsOrderMonitorState> WaitingExecuteOrderStates = new ConcurrentDictionary<string, clsOrderMonitorState>();
        private Logger logger = LogManager.GetCurrentClassLogger();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000);

                    DatabaseCaches.TaskCaches.WaitExecuteTasks.ForEach(task =>
                    {
                        if (!WaitingExecuteOrderStates.ContainsKey(task.TaskName))
                        {
                            double _timeout = AGVSConfigulator.SysConfigs.OrderState.TaskNoExecutedTimeout;
                            clsOrderMonitorState state = new clsOrderMonitorState(task, _timeout);
                            WaitingExecuteOrderStates.TryAdd(task.TaskName, state);
                            state.OnNormalWatchEnd += (sender, e) =>
                            {
                                logger.Info($"{state.Order.TaskName} Task State Watch Normal End");
                            };
                            state.OnTimeout += (sender, e) =>
                            {
                                logger.Error($"{state.Order.TaskName} Wait Task Be Executed Timeout({state.Timeout} min)");
                            };
                            state?.StartCountDown();

                        }
                    });

                    DatabaseCaches.TaskCaches.RunningTasks.ForEach(task =>
                    {
                        StopWatchState(task);

                    });

                    DatabaseCaches.TaskCaches.CompleteTasks.ForEach(task =>
                    {
                        StopWatchState(task);
                    });

                    void StopWatchState(clsTaskDto task)
                    {
                        if (WaitingExecuteOrderStates.ContainsKey(task.TaskName))
                        {
                            if (WaitingExecuteOrderStates.TryRemove(task.TaskName, out clsOrderMonitorState? _state))
                            {
                                _state?.FinishCountDown();
                            }
                        }
                    }
                }
            });
        }


        public class clsOrderMonitorState
        {
            public clsTaskDto Order { get; }

            public readonly double Timeout = 20;

            internal event EventHandler OnNormalWatchEnd;
            internal event EventHandler OnTimeout;

            public clsOrderMonitorState(clsTaskDto order, double timeout = 20)
            {
                this.Order = order;
                this.Timeout = timeout;

            }

            private ManualResetEvent manualReset = new ManualResetEvent(false);
            internal void FinishCountDown()
            {
                manualReset.Set();
            }

            internal async Task StartCountDown()
            {
                _ = Task.Run(async () =>
                {
                    bool normalSet = manualReset.WaitOne(TimeSpan.FromMinutes(Timeout));
                    if (!normalSet)
                    {
                        OnTimeout?.Invoke(this, EventArgs.Empty);
                        clsAlarmDto alarmDto = await AlarmManagerCenter.AddAlarmAsync(ALARMS.WaitTaskBeExecutedTimeout, level: ALARM_LEVEL.WARNING, taskName: Order.TaskName, Equipment_Name: Order.DesignatedAGVName);
                        VMSManager.TaskCancel(this.Order.TaskName, $"{alarmDto.Description}");
                    }
                    else
                    {
                        OnNormalWatchEnd?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
        }
    }
}

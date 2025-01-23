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
        private ConcurrentDictionary<string, clsOrderMonitorState> RunningOrderMonitoringStates = new ConcurrentDictionary<string, clsOrderMonitorState>();
        private Logger logger = LogManager.GetCurrentClassLogger();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000);

                    //監視正在等待被執行的任務
                    DatabaseCaches.TaskCaches.WaitExecuteTasks.ForEach(task =>
                    {
                        if (!WaitingExecuteOrderStates.ContainsKey(task.TaskName))
                        {
                            double _timeout = AGVSConfigulator.SysConfigs.OrderState.TaskNoExecutedTimeout;

                            clsOrderMonitorState state = new clsOrderMonitorState(task, _timeout)
                            {
                                TimeoutAction = AGVSConfigulator.SysConfigs.OrderState.CancelTaskWhenTaskNoExecutedTimeout ? clsOrderMonitorState.TIMEOUT_ACTION.CANCEL_TASK : clsOrderMonitorState.TIMEOUT_ACTION.JUST_ADD_WARNING
                            };
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

                    //監視正在運行的任務
                    DatabaseCaches.TaskCaches.RunningTasks.ForEach(task =>
                    {
                        if (!RunningOrderMonitoringStates.ContainsKey(task.TaskName))
                        {
                            double _timeout = AGVSConfigulator.SysConfigs.OrderState.TaskDoActionTimeout;
                            clsOrderMonitorState state = new clsOrderMonitorState(task, _timeout)
                            {
                                AlarmCode = ALARMS.OrderExecuteTimeout,
                                TimeoutAction = AGVSConfigulator.SysConfigs.OrderState.CancelTaskWhenTaskDoActionTimeout ? clsOrderMonitorState.TIMEOUT_ACTION.CANCEL_TASK : clsOrderMonitorState.TIMEOUT_ACTION.JUST_ADD_WARNING
                            };
                            RunningOrderMonitoringStates.TryAdd(task.TaskName, state);
                            state.OnNormalWatchEnd += (sender, e) =>
                            {
                                logger.Info($"{state.Order.TaskName} Order Running Time Watch Normal End");
                            };
                            state.OnTimeout += (sender, e) =>
                            {
                                logger.Error($"{state.Order.TaskName} Order Running Timeout! ({state.Timeout} min)");
                            };
                            state?.StartCountDown();
                        }

                    });

                    DatabaseCaches.TaskCaches.RunningTasks.ForEach(task =>
                    {
                        StopWaitingTaskWatchState(task);
                    });

                    DatabaseCaches.TaskCaches.CompleteTasks.ForEach(task =>
                    {
                        StopWaitingTaskWatchState(task);
                        StopRunningTaskWatchState(task);
                    });

                    void StopWaitingTaskWatchState(clsTaskDto task)
                    {
                        if (WaitingExecuteOrderStates.ContainsKey(task.TaskName))
                        {
                            if (WaitingExecuteOrderStates.TryRemove(task.TaskName, out clsOrderMonitorState? _state))
                            {
                                _state?.FinishCountDown();
                            }
                        }
                    }

                    void StopRunningTaskWatchState(clsTaskDto task)
                    {
                        if (RunningOrderMonitoringStates.ContainsKey(task.TaskName))
                        {
                            if (RunningOrderMonitoringStates.TryRemove(task.TaskName, out clsOrderMonitorState? _state))
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

            public enum TIMEOUT_ACTION
            {
                JUST_ADD_WARNING,
                CANCEL_TASK
            }

            public clsTaskDto Order { get; }
            public readonly double Timeout = 20;
            public TIMEOUT_ACTION TimeoutAction = TIMEOUT_ACTION.JUST_ADD_WARNING;
            internal event EventHandler OnNormalWatchEnd;
            internal event EventHandler OnTimeout;

            public ALARMS AlarmCode = ALARMS.WaitTaskBeExecutedTimeout;

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
                        clsAlarmDto alarmDto = await AlarmManagerCenter.AddAlarmAsync(AlarmCode, level: ALARM_LEVEL.WARNING, taskName: Order.TaskName, Equipment_Name: Order.DesignatedAGVName);
                        if (TimeoutAction == TIMEOUT_ACTION.CANCEL_TASK)
                            VMSManager.TaskCancel(this.Order.TaskName, false, $"{alarmDto.Description}");
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

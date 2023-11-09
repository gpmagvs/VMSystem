using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.RunMode;
using AGVSystemCommonNet6.Log;

namespace VMSystem
{
    public static class SystemModes
    {
        internal static Action OnRunModeON;
        internal static Action OnRunModeOFF;
        private static RUN_MODE _RunMode = RUN_MODE.MAINTAIN;
        internal static RUN_MODE RunMode
        {
            get => _RunMode;
            set
            {
                if (_RunMode != value)
                {
                    _RunMode = value;
                    LOG.INFO($"Run Mode Switch to {_RunMode}");
                    if (_RunMode == RUN_MODE.RUN)
                    {
                        if (OnRunModeON != null)
                            OnRunModeON();
                    }
                    else
                    {
                        if (OnRunModeOFF != null)
                            OnRunModeOFF();
                    }
                }
            }
        }
        internal static HOST_CONN_MODE HostConnMode { get; set; }
        internal static HOST_OPER_MODE HostOperMode { get; set; }

        public static bool RunModeSwitch(RUN_MODE mode, out string Message)
        {
            Message = string.Empty;
            RunMode = mode;
            return true;
        }
    }
}

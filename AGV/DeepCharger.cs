using System.Diagnostics;

namespace VMSystem.AGV
{
    public class DeepCharger
    {
        private bool _IsDeepCharging = false;
        private Stopwatch _DeepChargeStopWatcher = new Stopwatch();
        public void StartDeepCharging()
        {
            _IsDeepCharging = true;
            _DeepChargeStopWatcher.Restart();
        }

        public void StopDeepCharging()
        {
            _IsDeepCharging = false;
            _DeepChargeStopWatcher.Stop();
        }

        public bool GetIsDeepCharging()
        {
            return _IsDeepCharging;
        }
    }
}

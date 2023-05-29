using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.ViewModel
{
    public class VMSViewModel : IDisposable
    {
        private bool disposedValue;

        public RunningStatus RunningStatus { get; set; }
        public ONLINE_STATE OnlineStatus { get; set; }
        public VMSBaseProp BaseProps { get; set; }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                }

                RunningStatus = null;
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

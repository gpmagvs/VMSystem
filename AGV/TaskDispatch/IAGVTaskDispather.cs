using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.clsEnums;
using static VMSystem.AGV.clsAGVTaskDisaptchModule;

namespace VMSystem.AGV.TaskDispatch
{
    public interface IAGVTaskDispather
    {
        List<clsTaskDto> taskList { get; set; }
        MapPoint[] CurrentTrajectory { get; }
        Task<int> TaskFeedback(FeedbackData feedbackData);
        Task<string> CancelTask();
        Task<SimpleRequestResponse> PostTaskRequestToAGVAsync(clsTaskDownloadData request);
        void DispatchTrafficTask(clsTaskDownloadData task_download_data);
        AGV_ORDERABLE_STATUS OrderExecuteState { get; }
        public clsAGVTaskTrack TaskStatusTracker { get; set; }
        string ExecutingTaskName { get; set; }
    }

    public class clsWaitingInfo
    {
        [NonSerialized]
        public IAGV Agv;

        [NonSerialized]
        public static Action<clsWaitingInfo> OnAGVWaitingStatusChanged;
        private bool _IsWaiting = false;
        public bool IsWaiting
        {
            get => _IsWaiting;
            set
            {
                if (_IsWaiting != value)
                {
                    _IsWaiting = value;
                    if (OnAGVWaitingStatusChanged != null)
                        OnAGVWaitingStatusChanged(this);
                }
            }
        }
        public int ParkingTag { get; private set; }
        public MapPoint WaitingPoint { get; internal set; } = new MapPoint();
        public string Descrption { get; private set; } = "";

        public DateTime StartWaitingTime { get; private set; }
        public void SetStatusNoWaiting(IAGV Agv)
        {
            this.Agv = Agv;
            this.IsWaiting = false;
        }
        public void SetStatusGoToWaitingPoint(IAGV Agv, int parkingTag, MapPoint ConflicPoint)
        {
            this.Agv = Agv;
            this.WaitingPoint = ConflicPoint;
            this.IsWaiting = true;
            this.ParkingTag = parkingTag;
            Descrption = $"前往-{parkingTag} 等待-{ConflicPoint.TagNumber}可通行";
        }
        public void SetStatusWaitingConflictPointRelease(IAGV Agv, int parkingTag, MapPoint ConflicPoint)
        {
            this.Agv = Agv;
            this.WaitingPoint = ConflicPoint;
            this.IsWaiting = true;
            this.ParkingTag = parkingTag;
            Descrption = $"等待-{ConflicPoint.TagNumber}可通行";
            StartWaitingTime = DateTime.Now;
        }
    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using static AGVSystemCommonNet6.clsEnums;
using static VMSystem.AGV.clsAGVTaskDisaptchModule;

namespace VMSystem.AGV.TaskDispatch
{
    public interface IAGVTaskDispather
    {
        List<clsTaskDto> taskList { get; set; }
        MapPoint[] CurrentTrajectory { get;}
        Task<int> TaskFeedback(FeedbackData feedbackData);
        void CancelTask();
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
        public MapPoint WaitingPoint { get; internal set; } = new MapPoint();
        public string Descrption { get; set; } = "";

        public void UpdateInfo(IAGV Agv, bool IsWaiting, string descrption = "", MapPoint WaitingPoint = null)
        {
            this.Agv = Agv;
            this.Descrption = descrption;
            this.WaitingPoint = WaitingPoint == null ? this.WaitingPoint : WaitingPoint;
            this.IsWaiting = IsWaiting;
        }
    }
}

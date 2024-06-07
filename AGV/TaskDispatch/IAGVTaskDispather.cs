using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using System.Text.Json.Serialization;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using static AGVSystemCommonNet6.clsEnums;
using static VMSystem.AGV.clsAGVTaskDisaptchModule;

namespace VMSystem.AGV.TaskDispatch
{
    public interface IAGVTaskDispather
    {
        public enum WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY
        {
            PLEASE_WAIT,
            PLEASE_YIELD_ME
        }
        Task Run();
        List<clsTaskDto> taskList { get; }
        MapPoint[] CurrentTrajectory { get; }
        Task<int> TaskFeedback(FeedbackData feedbackData);
        Task<string> CancelTask(bool unRegistPoints = true);
        Task<SimpleRequestResponse> PostTaskRequestToAGVAsync(clsTaskDownloadData request);
        void DispatchTrafficTask(clsTaskDownloadData task_download_data);
        WAITING_FOR_MOVE_AGV_CONFLIC_ACTION_REPLY AGVWaitingYouNotify(IAGV agv);
        void AGVNotWaitingYouNotify(IAGV agv);

        AGV_ORDERABLE_STATUS OrderExecuteState { get; set; }
        public clsAGVTaskTrack TaskStatusTracker { get; set; }
        public OrderHandlerBase OrderHandler { get; set; }
        public clsAGVTaskTrack LastNormalTaskPauseByAvoid { get; set; }
        string ExecutingTaskName { get; set; }

        Dictionary<int, List<MapPoint>> Dict_PathNearPoint { get; set; }

        public void TryAppendTasksToQueue(List<clsTaskDto> tasksCollection);
        void AsyncTaskQueueFromDatabase();
    }

    public class clsWaitingInfo
    {
        public enum WAIT_STATUS
        {
            NO_WAIT, GO_TO_WAIT_PT, WAITING
        }
        [JsonIgnore]
        [NonSerialized]
        internal IAGV Agv;
        public WAIT_STATUS Status { get; set; }
        [JsonIgnore]
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
                }
            }
        }


        [JsonIgnore]
        internal ManualResetEvent AllowMoveResumeResetEvent = new ManualResetEvent(false);
        public int ParkingTag { get; private set; }


        [JsonIgnore]
        public MapPoint WaitingPoint { get; internal set; } = new MapPoint();
        public string Descrption { get; private set; } = "";

        public DateTime StartWaitingTime { get; private set; }
        public clsWaitingInfo()
        {

        }
        public clsWaitingInfo(IAGV Agv)
        {
            this.Agv = Agv;
        }
        public void SetStatusNoWaiting()
        {
            SetStatusNoWaiting(this.Agv);
        }
        public void SetStatusNoWaiting(IAGV Agv)
        {
            Status = WAIT_STATUS.NO_WAIT;
            this.Agv = Agv;
            this.IsWaiting = false;
            //if (OnAGVWaitingStatusChanged != null)
            //    OnAGVWaitingStatusChanged(this);
        }
        public void SetStatusGoToWaitingPoint(int parkingTag, MapPoint ConflicPoint)
        {
            SetStatusGoToWaitingPoint(this.Agv, parkingTag, ConflicPoint);
        }
        public void SetStatusGoToWaitingPoint(IAGV Agv, int parkingTag, MapPoint ConflicPoint)
        {
            Status = WAIT_STATUS.GO_TO_WAIT_PT;
            this.Agv = Agv;
            this.WaitingPoint = ConflicPoint;
            this.IsWaiting = true;
            this.ParkingTag = parkingTag;
            Descrption = $"前往-{parkingTag} 等待-{ConflicPoint.TagNumber}可通行";
        }
        public void SetStatusWaitingConflictPointRelease(int parkingTag, MapPoint ConflicPoint)
        {
            SetStatusWaitingConflictPointRelease(this.Agv, parkingTag, ConflicPoint);
        }
        public void SetStatusWaitingConflictPointRelease(IAGV Agv, int parkingTag, MapPoint ConflicPoint)
        {
            this.Agv = Agv;
            this.WaitingPoint = ConflicPoint;
            this.IsWaiting = true;
            this.ParkingTag = parkingTag;
            Descrption = $"等待-{ConflicPoint.TagNumber}可通行";
            StartWaitingTime = DateTime.Now;
            Status = WAIT_STATUS.WAITING;
            AllowMoveResumeResetEvent.Reset();
            if (OnAGVWaitingStatusChanged != null)
                OnAGVWaitingStatusChanged(this);
        }

        internal void SetStatusWaitingConflictPointRelease(List<int> blockedTags)
        {
            SetStatusWaitingConflictPointRelease(blockedTags, $"等待-{string.Join(",", blockedTags)}可通行");
        }

        internal void SetStatusWaitingConflictPointRelease(List<int> blockedTags, string customMessage)
        {
            this.IsWaiting = true;
            this.WaitingPoint = new MapPoint("", -1);
            Descrption = customMessage;
            StartWaitingTime = DateTime.Now;
            Status = WAIT_STATUS.WAITING;
        }

        internal void SetDisplayMessage(string message)
        {
            SetStatusWaitingConflictPointRelease(new List<int>(), message);
        }
    }
}

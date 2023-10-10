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
        clsMapPoint[] CurrentTrajectory { get; set; }
        Task<int> TaskFeedback(FeedbackData feedbackData);
        void CancelTask();
        Task<SimpleRequestResponse> PostTaskRequestToAGVAsync(clsTaskDownloadData request);
        void DispatchTrafficTask(clsTaskDownloadData task_download_data);
        AGV_ORDERABLE_STATUS OrderExecuteState { get; }
        public clsAGVTaskTrack TaskStatusTracker { get; set; }
    }

    public class clsWaitingInfo
    {
        public bool IsWaiting { get; set; } = false;
        public MapPoint WaitingPoint { get; internal set; } = new MapPoint();
    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.MAP;
using VMSystem.Dispatch;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class NavigateToGoalTask : TaskBase
    {
        public NavigateToGoalTask()
        {

        }

        public NavigateToGoalTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling;

        public override ACTION_TYPE ActionType => throw new NotImplementedException();

        public override async Task SendTaskToAGV()
        {
            DestineTag = Stage == VehicleMovementStage.Traveling_To_Source ? OrderData.From_Station_Tag : OrderData.To_Station_Tag;


        }

        private async Task Navigation(MapPoint currentPoint, MapPoint goalPoint, List<MapPoint> passedMapPoints)
        {
            IEnumerable<MapPoint> pathResponse = await DispatchCenter.MoveToDestineDispatchRequest(Agv, currentPoint, OrderData, Stage);

            if (pathResponse == null)
                await Navigation(Agv.currentMapPoint, goalPoint, passedMapPoints);

            //prepare trajectory
            List<MapPoint> _SendToPoints = new List<MapPoint>();
            _SendToPoints.AddRange(passedMapPoints);
            _SendToPoints.AddRange(pathResponse.ToList());
            _SendToPoints = _SendToPoints.DistinctBy(pt => pt.TagNumber).ToList();

            Agv.TaskExecuter.WaitACTIONFinishReportedMRE.Reset();
            //create clsTaskDownloadData and send to AGV
            TaskDownloadRequestResponse agvResponse = await Agv.TaskExecuter.TaskDownload(this, new clsTaskDownloadData
            {
                Action_Type = ACTION_TYPE.None,
                CST = new clsCST[] { new clsCST { CST_ID = OrderData.Carrier_ID, CST_Type = OrderData.CST_TYPE == 200 ? CST_TYPE.Tray : CST_TYPE.Rack } },
                Task_Name = OrderData.TaskName,
                Destination = goalPoint.TagNumber,
                Height = OrderData.Height,
                Trajectory = PathFinder.GetTrajectory(_SendToPoints)
            });

            //check response from AGV and update passedMapPoints
            if (agvResponse.ReturnCode == TASK_DOWNLOAD_RETURN_CODES.OK)
            {
                passedMapPoints = _SendToPoints.Clone();
                Agv.TaskExecuter.WaitACTIONFinishReportedMRE.WaitOne();
            }
            else
            {
                // NG 
            }

        }

        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
        }

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }


    }
}

using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using System.Diagnostics;
using System.Xml.Linq;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MeasureTask : TaskBase
    {
        public MeasureTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.MeasureInBay;

        public override ACTION_TYPE ActionType => ACTION_TYPE.Measure;

        public List<int> TagsOfMeasureCompletedPoint { get; private set; } = new List<int>();
        public bool IsAllPointMeasured
        {
            get
            {
                var measurePointsNum = this.TaskDonwloadToAGV.Homing_Trajectory.Length - 1;
                return TagsOfMeasureCompletedPoint.Count == measurePointsNum;
            }
        }
        public override void CreateTaskToAGV()
        {
            base.CreateTaskToAGV();
            string bayName = OrderData.To_Station;

            if (StaMap.Map.Bays.TryGetValue(bayName, out Bay _bay))
            {
                var _ptList = _bay.Points.ToList();
                _ptList.Insert(0, _bay.InPoint);
                MapPoint outPoint = StaMap.GetPointByName(_bay.OutPoint);
                clsMapPoint[] homingTrajectory = _ptList.Select(name => MapPointToTaskPoint(StaMap.GetPointByName(name))).ToArray();
                this.TaskDonwloadToAGV.Destination = homingTrajectory.First().Point_ID;
                this.TaskDonwloadToAGV.Homing_Trajectory = homingTrajectory;
                this.TaskDonwloadToAGV.InpointOfEnterWorkStation = homingTrajectory[0];
                this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation = MapPointToTaskPoint(outPoint);
            }

        }
        private bool AGVTaskDoneFlag = false;
        public override async Task SendTaskToAGV()
        {
            try
            {
                Agv.OnMapPointChanged += Agv_OnMapPointChanged;
                await base.SendTaskToAGV();
                AGVTaskDoneFlag = false;
                await WaitAGVTaskDone();
                AGVTaskDoneFlag = true;
            }
            catch (Exception ex)
            {
                throw new TaskCanceledException(ex.Message);
            }
            finally
            {
                Agv.OnMapPointChanged -= Agv_OnMapPointChanged;
            }
        }

        private void Agv_OnMapPointChanged(object? sender, int tag)
        {
            try
            {
                bool isReachMeasureLeavingPoint = tag == TaskDonwloadToAGV.OutPointOfLeaveWorkstation.Point_ID;
                if (isReachMeasureLeavingPoint)
                {
                    NotifyServiceHelper.INFO($"{Agv.Name} Finish Measure Task And Leave Bay!");
                    Stopwatch _watchActionFinish = Stopwatch.StartNew();
                    Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        while (!AGVTaskDoneFlag)
                        {
                            await Task.Delay(1000);
                            if (_watchActionFinish.Elapsed.TotalSeconds > 5)
                            {
                                _WaitAGVTaskDoneMRE.Set();
                                _watchActionFinish.Stop();
                                NotifyServiceHelper.WARNING($"{Agv.Name} Leave Bay But Task Not Done.Forcing Done..");
                                return;
                            }
                        }
                        NotifyServiceHelper.INFO($"{Agv.Name} Finish Measure Task!");

                    });
                }
                else
                {
                    NotifyServiceHelper.INFO($"{Agv.Name} Reach Tag {tag} And Start Measure!");
                }
            }
            catch (Exception ex)
            {
                NotifyServiceHelper.ERROR($"{Agv.Name} Point Changed Handle Of Measure Task Exception.({ex.Message})");

                logger.Error(ex);
            }
        }

        public override bool IsThisTaskDone(FeedbackData feedbackData)
        {
            if (!base.IsThisTaskDone(feedbackData))
                return false;

            return this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation.Point_ID == Agv.currentMapPoint.TagNumber;
        }

        public override bool IsAGVReachDestine
        {
            get
            {
                var agvCurrentTag = Agv.states.Last_Visited_Node;
                var finalTag = this.TaskDonwloadToAGV.OutPointOfLeaveWorkstation.Point_ID;
                var isReach = agvCurrentTag == finalTag;

                logger.Info($"Check AGV is reach final goal ,RESULT:{isReach} || AGV at {agvCurrentTag}/Goal:{finalTag}");
                return isReach;
            }
        }
        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
            throw new NotImplementedException();
        }

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
            throw new NotImplementedException();
        }

        internal void UpdateMeasureProgress(int tagID)
        {
            NotifyServiceHelper.SUCCESS($"{Agv.Name}完成量測上報(Tag:{tagID})");

            TagsOfMeasureCompletedPoint.Add(tagID);
        }

        internal override bool CheckCargoStatus(out ALARMS alarmCode)
        {
            alarmCode = ALARMS.NONE;
            return true;
        }
    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Notify;
using VMSystem.AGV.TaskDispatch.Tasks;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV.TaskDispatch.OrderHandler
{
    public class MeasureOrderHandler : OrderHandlerBase
    {
        public MeasureOrderHandler() : base() { }
        public MeasureOrderHandler(SemaphoreSlim taskTbModifyLock) : base(taskTbModifyLock)
        {
        }

        public override ACTION_TYPE OrderAction => ACTION_TYPE.Measure;

        internal override List<int> GetNavPathTags()
        {
            try
            {

                var currentAction = RunningTask.ActionType;
                if (currentAction == ACTION_TYPE.None)
                {
                    return base.GetNavPathTags();
                }
                else
                {
                    MeasureTask measureTask = (RunningTask as MeasureTask);
                    var homingTrajTags = measureTask.TaskDonwloadToAGV.Homing_Trajectory.Select(pt => pt.Point_ID).ToList();

                    List<int> pathTags = new List<int>();
                    if (measureTask.IsAllPointMeasured)
                    {
                        //Now is goto outpoint
                        var lastMeasurePtTag = homingTrajTags.Last();
                        var inPointTag = measureTask.TaskDonwloadToAGV.InpointOfEnterWorkStation.Point_ID;
                        var outPointTag = measureTask.TaskDonwloadToAGV.OutPointOfLeaveWorkstation.Point_ID;
                        bool isBackToInPoint = inPointTag == outPointTag;

                        if (isBackToInPoint)
                        {
                            var _homingReversedTags = homingTrajTags.Clone();
                            _homingReversedTags.Reverse();
                            _homingReversedTags.Add(inPointTag);
                            pathTags = _homingReversedTags;
                        }
                        else
                            pathTags = new List<int> { lastMeasurePtTag, outPointTag };
                    }
                    else
                    {
                        //Now is go to next measure point
                        pathTags = homingTrajTags;
                    }
                    var remainTags = base.GetNavPathTags(pathTags);
                    RunningTask.FuturePlanNavigationTags = remainTags;
                    return remainTags;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return new List<int>();
            }
        }
        protected override void HandleAGVNavigatingFeedback(FeedbackData feedbackData)
        {
            base.HandleAGVNavigatingFeedback(feedbackData);
        }
        protected override async Task HandleAGVActionFinishFeedback()
        {
            logger.Info($"{Agv.Name} Feedback Action Finish of Measure Order (Current Location:{Agv.currentMapPoint.TagNumber})");
            if (RunningTask.IsAGVReachDestine && Agv.main_state != MAIN_STATUS.DOWN)
            {
                RunningTask.ActionFinishInvoke();
                _CurrnetTaskFinishResetEvent.Set();
            }
            else if (Agv.main_state == MAIN_STATUS.DOWN)
                AbortOrder(Agv.states.Alarm_Code);
        }
        protected override void HandleAGVActionStartFeedback()
        {
            base.HandleAGVActionStartFeedback();
            RunningTask.TrafficWaitingState.SetDisplayMessage($"量測任務進行中...");
        }
        internal async Task MeasureResultFeedback(clsMeasureResult measureResult)
        {
            logger.Info($"{Agv.Name} Report Measure Data: {measureResult.ToJson()}");
            string BayName = StaMap.GetBayNameByMesLocation(measureResult.location);
            measureResult.AGVName = Agv.Name;
            measureResult.BayName = BayName;
            await SaveMeasureReusltToDatabase(measureResult);

            if (RunningTask.ActionType == ACTION_TYPE.Measure)
            {
                (RunningTask as MeasureTask).UpdateMeasureProgress(measureResult.TagID);
            }
        }

        private async Task SaveMeasureReusltToDatabase(clsMeasureResult measureResult)
        {
            using (var database = new AGVSDatabase())
            {
                try
                {
                    database.tables.InstrumentMeasureResult.Add(measureResult);
                    await database.SaveChanges();
                }
                catch (Exception ex)
                {
                    logger.Error(ex.Message, ex);
                    AlarmManagerCenter.AddAlarmAsync(ALARMS.Save_Measure_Data_to_DB_Fail, ALARM_SOURCE.AGVS, ALARM_LEVEL.WARNING);
                }
            }
        }
    }
}

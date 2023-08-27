﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpHelper;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.AGV.TaskDispatch
{
    /// <summary>
    /// 追蹤AGV任務鍊
    /// </summary>
    public class clsAGVTaskTrack
    {
        public IAGV AGV;
        protected TaskDatabaseHelper TaskDBHelper = new TaskDatabaseHelper();

        public clsTaskDto TaskOrder;
        public string OrderTaskName { get; private set; } = "";
        public ACTION_TYPE TaskAction => TaskOrder == null ? ACTION_TYPE.None : TaskOrder.Action;

        private PathFinder pathFinder = new PathFinder();
        public clsWaitingInfo waitingInfo { get; set; } = new clsWaitingInfo();
        public clsPathInfo TrafficInfo { get; set; } = new clsPathInfo();
        public ACTION_TYPE[] TrackingActions => SubTasks.Select(subtask => subtask.Action).ToArray();
        private int taskSequence = 0;
        public List<int> RemainTags
        {
            get
            {
                try
                {

                    if (TaskOrder == null | SubTaskTracking == null)
                        return new List<int>();

                    var currentindex = SubTaskTracking.DownloadData.ExecutingTrajecory.ToList().FindIndex(pt => pt.Point_ID == AGV.currentMapPoint.TagNumber);
                    if (currentindex < 0)
                        return new List<int>();
                    var remian_traj = new clsMapPoint[SubTaskTracking.DownloadData.ExecutingTrajecory.Length - currentindex];
                    SubTaskTracking.DownloadData.ExecutingTrajecory.ToList().CopyTo(currentindex, remian_traj, 0, remian_traj.Length);
                    return remian_traj.Select(r => r.Point_ID).ToList();
                }
                catch (Exception ex)
                {
                    return new List<int>();
                }
            }
        }

        private int finishSubTaskNum = 0;

        private ACTION_TYPE previousCompleteAction = ACTION_TYPE.Unknown;
        private ACTION_TYPE carryTaskCompleteAction = ACTION_TYPE.Unknown;
        public ACTION_TYPE currentActionType { get; private set; } = ACTION_TYPE.Unknown;
        public ACTION_TYPE nextActionType { get; private set; } = ACTION_TYPE.Unknown;
        private CancellationTokenSource taskCancel = new CancellationTokenSource();

        private TASK_RUN_STATUS _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;
        public TASK_RUN_STATUS TaskRunningStatus
        {
            get => _TaskRunningStatus;
            set
            {
                if (_TaskRunningStatus != value)
                {
                    _TaskRunningStatus = value;
                    if (_TaskRunningStatus == TASK_RUN_STATUS.CANCEL)
                    {
                        CancelOrder();

                    }
                }
            }
        }


        private string HttpHost => $"http://{AGV.options.HostIP}:{AGV.options.HostPort}";
        public clsAGVTaskTrack()
        {

            StartTaskStatusWatchDog();
        }

        private void StartTaskStatusWatchDog()
        {
            Task.Run(async () =>
            {
                MAIN_STATUS agv_status = MAIN_STATUS.Unknown;
                while (true)
                {
                    Thread.Sleep(1);
                    if (TaskOrder == null)
                        continue;

                    TaskRunningStatus = TaskDBHelper.GetTaskStateByID(OrderTaskName);

                }
            });
        }
        public Queue<clsSubTask> SubTasks = new Queue<clsSubTask>();

        public Stack<clsSubTask> CompletedSubTasks = new Stack<clsSubTask>();

        public clsSubTask SubTaskTracking;
        public void Start(IAGV AGV, clsTaskDto TaskOrder)
        {
            try
            {
                this.TaskOrder = TaskOrder;
                OrderTaskName = TaskOrder.TaskName;
                finishSubTaskNum = 0;
                taskCancel = new CancellationTokenSource();
                taskSequence = 0;
                waitingInfo.IsWaiting = false;
                SubTasks = CreateSubTaskLinks(TaskOrder);
                CompletedSubTasks = new Stack<clsSubTask>();
                StartExecuteOrder();
                StartRecordTrjectory();
                LOG.INFO($"{AGV.Name}- {TaskOrder.Action} 訂單開始,動作:{string.Join("->", TrackingActions)}");
            }
            catch (IlleagalTaskDispatchException ex)
            {
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: OrderTaskName, location: AGV.currentMapPoint.Name);
            }
        }

        private void StartExecuteOrder()
        {
            taskSequence = 0;
            DownloadTaskToAGV();
        }

        private void DownloadTaskToAGV()
        {
            var _task = SubTasks.Dequeue();
            _task.Source = AGV.currentMapPoint;
            _task.StartAngle = AGV.states.Coordination.Theta;
            _task.CreateTaskToAGV(TaskOrder, taskSequence);
            PostTaskRequestToAGVAsync(_task);
            SubTaskTracking = _task;
            taskSequence += 1;
        }

        /// <summary>
        /// 生成任務鏈
        /// </summary>
        /// <returns></returns>
        private Queue<clsSubTask> CreateSubTaskLinks(clsTaskDto taskOrder)
        {
            bool isCarry = taskOrder.Action == ACTION_TYPE.Carry;
            Queue<clsSubTask> task_links = new Queue<clsSubTask>();
            var agvLocating_station_type = AGV.currentMapPoint.StationType;
            if (agvLocating_station_type != STATION_TYPE.Normal)
            {

                var destine = StaMap.GetPointByIndex(AGV.currentMapPoint.Target.Keys.First());
                var subTask_move_out_from_workstation = new clsSubTask()
                {
                    Destination = destine,
                    StartAngle = AGV.currentMapPoint.Direction,
                    DestineStopAngle = AGV.currentMapPoint.Direction,
                };
                if (agvLocating_station_type == STATION_TYPE.Charge)
                    subTask_move_out_from_workstation.Action = ACTION_TYPE.Discharge;
                else
                    subTask_move_out_from_workstation.Action = ACTION_TYPE.Unpark;
                task_links.Enqueue(subTask_move_out_from_workstation);
            }
            //移動任務
            MapPoint destine_move_to = null;
            double thetaToStop = 0;
            if (taskOrder.Action == ACTION_TYPE.None)
            {
                destine_move_to = StaMap.GetPointByTagNumber(int.Parse(taskOrder.To_Station));
                thetaToStop = destine_move_to.Direction;
            }
            else
            {
                var destine_station_tag_str = isCarry ? taskOrder.From_Station : taskOrder.To_Station;
                var point_of_workstation = StaMap.GetPointByTagNumber(int.Parse(destine_station_tag_str));
                var secondary_of_destine = StaMap.GetPointByIndex(point_of_workstation.Target.Keys.First());
                thetaToStop = point_of_workstation.Direction_Secondary_Point;
                destine_move_to = secondary_of_destine;
            }

            var subTask_move_to_ = new clsSubTask()
            {
                Destination = destine_move_to,
                Action = ACTION_TYPE.None,
                DestineStopAngle = thetaToStop,
                StartAngle = AGV.states.Coordination.Theta
            };
            task_links.Enqueue(subTask_move_to_);

            ///非移動之工位任務 //load unload park charge carry=
            if (taskOrder.Action != ACTION_TYPE.None && task_links.Last() != null)
            {
                var work_destine = StaMap.GetPointByTagNumber(int.Parse(isCarry ? taskOrder.From_Station : taskOrder.To_Station));
                clsSubTask subTask_working_station = new clsSubTask
                {
                    Destination = work_destine,
                    Action = taskOrder.Action == ACTION_TYPE.Carry ? ACTION_TYPE.Unload : taskOrder.Action,
                    DestineStopAngle = work_destine.Direction,
                    StartAngle = work_destine.Direction,
                };

                task_links.Enqueue(subTask_working_station);

                if (isCarry)
                {
                    var destine_point = StaMap.GetPointByTagNumber(int.Parse(taskOrder.To_Station));//終點
                    var destine_move_to_destine = StaMap.GetPointByIndex(destine_point.Target.Keys.First());//終點之二次定位點
                    //monve
                    clsSubTask subTask_move_to_load_workstation = new clsSubTask
                    {
                        Destination = destine_move_to_destine,
                        Action = ACTION_TYPE.None,
                        DestineStopAngle = destine_move_to_destine.Direction,
                        StartAngle = AGV.states.Coordination.Theta

                    };
                    //workstation destine
                    task_links.Enqueue(subTask_move_to_load_workstation);

                    clsSubTask subTask_load = new clsSubTask
                    {
                        Action = ACTION_TYPE.Load,
                        Source = destine_move_to_destine,
                        Destination = destine_point,
                        DestineStopAngle = destine_point.Direction,
                        StartAngle = destine_point.Direction,
                    };
                    task_links.Enqueue(subTask_load);
                }

            }
            return task_links;
        }

        /// <summary>
        /// 處理AGV任務回報
        /// </summary>
        /// <param name="feedbackData"></param>
        public async Task<TASK_FEEDBACK_STATUS_CODE> HandleAGVFeedback(FeedbackData feedbackData)
        {

            var task_simplex = feedbackData.TaskSimplex;
            var task_status = feedbackData.TaskStatus;

            LOG.INFO($"{AGV.Name} Feedback Task Status:{task_simplex} -{feedbackData.TaskStatus}-pt:{feedbackData.PointIndex}");
            if (AGV.main_state == MAIN_STATUS.DOWN)
            {
                taskCancel.Cancel();
                _ = PostTaskCancelRequestToAGVAsync(RESET_MODE.ABORT);
                AbortOrder();
                return TASK_FEEDBACK_STATUS_CODE.OK;
            }
            switch (task_status)
            {
                case TASK_RUN_STATUS.NO_MISSION:
                    break;
                case TASK_RUN_STATUS.NAVIGATING:
                    break;
                case TASK_RUN_STATUS.REACH_POINT_OF_TRAJECTORY:
                    break;
                case TASK_RUN_STATUS.ACTION_START:
                    break;
                case TASK_RUN_STATUS.ACTION_FINISH:
                    var orderStatus = IsTaskOrderCompleteSuccess(feedbackData);
                    CompletedSubTasks.Push(SubTaskTracking);
                    LOG.INFO($"Task Order Status: {orderStatus.ToJson()}");
                    if (orderStatus.Status == ORDER_STATUS.COMPLETED | orderStatus.Status == ORDER_STATUS.NO_ORDER)
                    {
                        CompleteOrder();
                        return TASK_FEEDBACK_STATUS_CODE.OK;
                    }
                    try
                    {
                        DownloadTaskToAGV();
                    }
                    catch (IlleagalTaskDispatchException ex)
                    {
                        AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: OrderTaskName, location: AGV.currentMapPoint.Name);
                    }
                    break;
                case TASK_RUN_STATUS.WAIT:
                    break;
                case TASK_RUN_STATUS.FAILURE:
                    break;
                case TASK_RUN_STATUS.CANCEL:
                    break;
                default:
                    break;
            }

            return TASK_FEEDBACK_STATUS_CODE.OK;
        }


        public enum ORDER_STATUS
        {
            EXECUTING,
            COMPLETED,
            FAILURE,
            NO_ORDER
        }

        public class clsOrderStatus
        {
            public ORDER_STATUS Status = ORDER_STATUS.NO_ORDER;
            public string FailureReason = "";
        }
        /// <summary>
        /// 判斷AGV是否順利完成訂單
        /// </summary>
        /// <returns></returns>
        private clsOrderStatus IsTaskOrderCompleteSuccess(FeedbackData feedbackData)
        {
            if (TaskOrder == null)
            {
                return new clsOrderStatus
                {
                    Status = ORDER_STATUS.NO_ORDER
                };
            }

            previousCompleteAction = SubTaskTracking.Action;
            var orderACtion = TaskOrder.Action;
            bool isOrderCompleted = false;
            string msg = string.Empty;
            switch (orderACtion)  //任務訂單的類型
            {
                case ACTION_TYPE.None: //一般移動訂單
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Unload:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.LoadAndPark:
                    break;
                case ACTION_TYPE.Forward:
                    break;
                case ACTION_TYPE.Backward:
                    break;
                case ACTION_TYPE.FaB:
                    break;
                case ACTION_TYPE.Measure:
                    break;
                case ACTION_TYPE.Load:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Charge:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Carry:
                    isOrderCompleted = (previousCompleteAction == ACTION_TYPE.Load) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Discharge:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Escape:
                    break;
                case ACTION_TYPE.Park:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.Unpark:
                    isOrderCompleted = (previousCompleteAction == orderACtion) && CheckAGVPose(out msg);
                    break;
                case ACTION_TYPE.ExchangeBattery:
                    break;
                case ACTION_TYPE.Hold:
                    break;
                case ACTION_TYPE.Break:
                    break;
                case ACTION_TYPE.Unknown:
                    break;
                default:
                    break;
            }
            finishSubTaskNum = isOrderCompleted ? finishSubTaskNum += 1 : finishSubTaskNum;
            return new clsOrderStatus
            {
                Status = isOrderCompleted ? ORDER_STATUS.COMPLETED : ORDER_STATUS.EXECUTING
            };
        }
        private bool CheckAGVPose(out string message)
        {
            message = string.Empty;
            if (SubTaskTracking.Action != ACTION_TYPE.None)
            {
                return true;
            }
            var _destinTheta = SubTaskTracking.DestineStopAngle;
            var destine_tag = SubTaskTracking.Destination.TagNumber;
            if (SubTaskTracking.Action != ACTION_TYPE.None)
            {
                _destinTheta = SubTaskTracking.Source.Direction;
                destine_tag = SubTaskTracking.Source.TagNumber;
            }
            if (AGV.currentMapPoint.TagNumber != destine_tag)
            {
                message = "AGV並未抵達目的地";
                LOG.WARN($"AGV並未抵達 {destine_tag} ");
                //return false;
            }
            var _agvTheta = AGV.states.Coordination.Theta;

            var theta_error = Math.Abs(_agvTheta - _destinTheta);
            theta_error = theta_error > 180 ? 360 - theta_error : theta_error;
            if (Math.Abs(theta_error) > 10)
            {
                message = $"{AGV.Name} 角度與目的地[{destine_tag}]角度設定誤差>10度({AGV.states.Coordination.Theta}/{_destinTheta})";
                LOG.WARN(message);
                return false;
            }

            return true;
        }


        public async Task<SimpleRequestResponse> PostTaskCancelRequestToAGVAsync(RESET_MODE mode)
        {
            try
            {
                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = mode,
                    Task_Name = OrderTaskName,
                    TimeStamp = DateTime.Now,
                };
                SimpleRequestResponse taskStateResponse = await Http.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"{HttpHost}/api/TaskDispatch/Cancel", reset_cmd);
                LOG.WARN($"取消{AGV.Name}任務-[{SubTaskTracking.DownloadData.Task_Simplex}]-[{mode}]-AGV Response : Return Code :{taskStateResponse.ReturnCode},Message : {taskStateResponse.Message}");
                return taskStateResponse;
            }
            catch (Exception ex)
            {
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }

        public SimpleRequestResponse PostTaskRequestToAGVAsync(clsSubTask subtask)
        {
            try
            {
                AGV.CheckAGVStatesBeforeDispatchTask(nextActionType, subtask.Destination);
                SimpleRequestResponse taskStateResponse = Http.PostAsync<SimpleRequestResponse, clsTaskDownloadData>($"{HttpHost}/api/TaskDispatch/Execute", subtask.DownloadData).Result;
                return taskStateResponse;
            }
            catch (IlleagalTaskDispatchException ex)
            {
                AbortOrder();
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: OrderTaskName, location: AGV.currentMapPoint.Name);
                return new SimpleRequestResponse { ReturnCode = RETURN_CODE.NG, Message = ex.Alarm_Code.ToString() };
            }
            catch (Exception ex)
            {
                LOG.Critical(ex);
                return new SimpleRequestResponse
                {
                    ReturnCode = RETURN_CODE.System_Error
                };
            }
        }

        internal void ChangeTaskStatus(TASK_RUN_STATUS status, string failure_reason = "")
        {
            if (TaskOrder == null)
                return;
            TaskOrder.State = status;
            if (status == TASK_RUN_STATUS.FAILURE | status == TASK_RUN_STATUS.CANCEL | status == TASK_RUN_STATUS.ACTION_FINISH)
            {
                EndReocrdTrajectory();
                waitingInfo.IsWaiting = false;
                TaskOrder.FailureReason = failure_reason;
                TaskOrder.FinishTime = DateTime.Now;
                TaskDBHelper.Update(TaskOrder);
                TaskOrder = null;
                _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;
            }
            else
            {
                TaskDBHelper.Update(TaskOrder);
            }
        }

        private void CompleteOrder()
        {

            ChangeTaskStatus(TASK_RUN_STATUS.ACTION_FINISH);
            taskCancel.Cancel();
        }
        internal void AbortOrder()
        {
            taskCancel.Cancel();
            ChangeTaskStatus(TASK_RUN_STATUS.FAILURE);
        }

        internal async void CancelOrder()
        {
            await PostTaskCancelRequestToAGVAsync(RESET_MODE.CYCLE_STOP);
            taskCancel.Cancel();
            ChangeTaskStatus(TASK_RUN_STATUS.CANCEL);

        }

        System.Timers.Timer TrajectoryStoreTimer;

        private void StartRecordTrjectory()
        {
            TrajectoryStoreTimer = new System.Timers.Timer()
            {
                Interval = 100
            };
            TrajectoryStoreTimer.Elapsed += TrajectoryStoreTimer_Elapsed;
            TrajectoryStoreTimer.Enabled = true;
        }
        private void EndReocrdTrajectory()
        {
            TrajectoryStoreTimer?.Stop();
            TrajectoryStoreTimer?.Dispose();
        }

        /// <summary>
        /// 儲存軌跡到資料庫
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TrajectoryStoreTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            StoreTrajectory();
        }
        private void StoreTrajectory()
        {
            if (TaskOrder == null)
            {
                EndReocrdTrajectory();
                return;
            }
            string taskID = TaskOrder.TaskName;
            string agvName = AGV.Name;
            double x = AGV.states.Coordination.X;
            double y = AGV.states.Coordination.Y;
            double theta = AGV.states.Coordination.Theta;
            TrajectoryDBStoreHelper helper = new TrajectoryDBStoreHelper();
            helper.StoreTrajectory(taskID, agvName, x, y, theta);
        }


    }


}

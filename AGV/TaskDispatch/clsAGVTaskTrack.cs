using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.TASK;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using RosSharp.RosBridgeClient;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.AGV.TaskDispatch.clsAGVTaskTrack;

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

        public clsAGVSimulation AgvSimulation = null;

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


        HttpHelper AGVHttp;

        public clsAGVTaskTrack(clsAGVTaskDisaptchModule DispatchModule = null)
        {
            if (DispatchModule != null)
            {
                AgvSimulation = new clsAGVSimulation(DispatchModule);
            }
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

                    TaskRunningStatus = await TaskDBHelper.GetTaskStateByID(OrderTaskName);

                }
            });
        }
        public Queue<clsSubTask> SubTasks = new Queue<clsSubTask>();

        public Stack<clsSubTask> CompletedSubTasks = new Stack<clsSubTask>();

        public clsSubTask SubTaskTracking;
        public async Task Start(IAGV AGV, clsTaskDto TaskOrder)
        {
            await Task.Run(() =>
            {
                try
                {
                    AGVHttp = new HttpHelper($"http://{AGV.options.HostIP}:{AGV.options.HostPort}");
                    this.TaskOrder = TaskOrder;
                    OrderTaskName = TaskOrder.TaskName;
                    finishSubTaskNum = 0;
                    taskCancel = new CancellationTokenSource();
                    taskSequence = 0;
                    SubTaskTracking = null;
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
            });

        }

        private void StartExecuteOrder()
        {
            UpdateTaskStartPointAndTime();
            taskSequence = 0;
            DownloadTaskToAGV();
        }

        private void UpdateTaskStartPointAndTime()
        {
            using (var agvs = new AGVSDatabase())
            {
                TaskOrder.StartTime = DateTime.Now;
                if (TaskOrder.Action != ACTION_TYPE.Carry)
                    TaskOrder.From_Station = AGV.currentMapPoint.Name;
                agvs.tables.Tasks.Update(TaskOrder);
                agvs.tables.SaveChanges();
            }

        }

        private async void DownloadTaskToAGV(bool isMovingSeqmentTask = false)
        {
            if (SubTasks.Count == 0 && !isMovingSeqmentTask)
            {
                AlarmManagerCenter.AddAlarm(ALARMS.SubTask_Queue_Empty_But_Try_DownloadTask_To_AGV);
                return;
            }
            TASK_DOWNLOAD_RETURN_CODES agv_task_return_code = default;

            agv_task_return_code = PostTaskRequestToAGVAsync(out var _task, isMovingSeqmentTask).ReturnCode;
            if (agv_task_return_code != TASK_DOWNLOAD_RETURN_CODES.OK && agv_task_return_code != TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE)
            {
                AbortOrder(agv_task_return_code);
                return;
            }
            if (taskSequence == 0)
            {
                TaskOrder.State = TASK_RUN_STATUS.NAVIGATING;
                using (var agvs = new AGVSDatabase())
                {
                    agvs.tables.Tasks.Update(TaskOrder);
                    agvs.tables.SaveChanges();
                }
            }
            SubTaskTracking = _task;

            if (agv_task_return_code == TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE)
            {
                await Task.Delay(1000);
                LOG.INFO($"AGV Already locate in end of trajectory!");
                HandleAGVFeedback(new FeedbackData
                {
                    TaskName = SubTaskTracking.DownloadData.Task_Name,
                    TaskSimplex = SubTaskTracking.DownloadData.Task_Simplex,
                    TaskStatus = TASK_RUN_STATUS.ACTION_FINISH,
                });
            }

        }

        /// <summary>
        /// 生成任務鏈
        /// </summary>
        /// <returns></returns>
        protected virtual Queue<clsSubTask> CreateSubTaskLinks(clsTaskDto taskOrder)
        {
            bool isCarry = taskOrder.Action == ACTION_TYPE.Carry;
            Queue<clsSubTask> task_links = new Queue<clsSubTask>();
            //退出工位任務
            var agvLocating_station_type = AGV.currentMapPoint.StationType;
            if (agvLocating_station_type != STATION_TYPE.Normal)
            {

                var destine = StaMap.GetPointByIndex(AGV.currentMapPoint.Target.Keys.First());
                var subTask_move_out_from_workstation = new clsSubTask()
                {
                    Source = AGV.currentMapPoint,
                    Destination = destine,
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
                //移動至工位
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
            };
            task_links.Enqueue(subTask_move_to_);

            ///非移動之工位任務 //load unload park charge carry=
            if (taskOrder.Action != ACTION_TYPE.None && task_links.Last() != null)
            {
                var work_destine = StaMap.GetPointByTagNumber(int.Parse(isCarry ? taskOrder.From_Station : taskOrder.To_Station));
                clsSubTask subTask_working_station = new clsSubTask
                {
                    Source = StaMap.GetPointByIndex(work_destine.Target.Keys.First()),
                    Destination = work_destine,
                    Action = taskOrder.Action == ACTION_TYPE.Carry ? ACTION_TYPE.Unload : taskOrder.Action,
                    DestineStopAngle = work_destine.Direction,
                    CarrierID = taskOrder.Carrier_ID

                };
                //工位任務-1:取/放貨
                task_links.Enqueue(subTask_working_station);

                if (isCarry)
                {
                    var workstation_point = StaMap.GetPointByTagNumber(int.Parse(taskOrder.To_Station));//終點
                    var secondary_of_destine_workstation = StaMap.GetPointByIndex(workstation_point.Target.Keys.First());//終點之二次定位點
                    //第二段移動任務 移動至工位
                    clsSubTask subTask_move_to_load_workstation = new clsSubTask
                    {
                        Destination = secondary_of_destine_workstation,
                        Action = ACTION_TYPE.None,
                        DestineStopAngle = workstation_point.Direction_Secondary_Point,

                    };
                    //工位任務-2:放貨
                    task_links.Enqueue(subTask_move_to_load_workstation);

                    clsSubTask subTask_load = new clsSubTask
                    {
                        Action = ACTION_TYPE.Load,
                        Source = secondary_of_destine_workstation,
                        Destination = workstation_point,
                        DestineStopAngle = workstation_point.Direction,
                        CarrierID = taskOrder.Carrier_ID
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
                AbortOrder(TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN);
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
                    if (orderStatus.Status == ORDER_STATUS.COMPLETED | orderStatus.Status == ORDER_STATUS.NO_ORDER)
                    {
                        CompletedSubTasks.Push(SubTaskTracking);
                        CompleteOrder();
                        return TASK_FEEDBACK_STATUS_CODE.OK;
                    }
                    else if (orderStatus.Status == ORDER_STATUS.EXECUTING_WAITING)
                    {
                        try
                        {
                            waitingInfo.IsWaiting = true;
                            waitingInfo.WaitingPoint = SubTaskTracking.GetNextPointToGo(orderStatus.AGVLocation);
                            waitingInfo.Descrption = $"等待-{waitingInfo.WaitingPoint.TagNumber}可通行";
                            WaitingRegistReleaseAndGo();
                            break;
                        }
                        catch (Exception ex)
                        {
                        }
                    }
                    else
                    {
                        taskSequence += 1;
                        DownloadTaskToAGV();
                    }
                    LOG.INFO($"Task Order Status: {orderStatus.ToJson()}");
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

        private void WaitingRegistReleaseAndGo()
        {
            Task.Factory.StartNew(() =>
            {
                while (waitingInfo.WaitingPoint.RegistInfo.IsRegisted && waitingInfo.WaitingPoint.RegistInfo.RegisterAGVName != AGV.Name)
                {
                    Thread.Sleep(1000);
                    if (taskCancel.IsCancellationRequested)
                    {
                        LOG.INFO($"任務已取消結束等待");
                        return;
                    }
                }
                if (taskCancel.IsCancellationRequested)
                {
                    LOG.INFO($"任務已取消結束等待");
                    return;
                }
                LOG.INFO($"{waitingInfo.WaitingPoint.Name}已解除註冊,任務下發");
                waitingInfo = new clsWaitingInfo();
                DownloadTaskToAGV(true);
            });
        }

        public enum ORDER_STATUS
        {
            EXECUTING,
            COMPLETED,
            FAILURE,
            NO_ORDER,
            EXECUTING_WAITING
        }

        public class clsOrderStatus
        {
            public ORDER_STATUS Status = ORDER_STATUS.NO_ORDER;
            public string FailureReason = "";

            public MapPoint AGVLocation { get; internal set; }
        }
        /// <summary>
        /// 判斷AGV是否順利完成訂單
        /// </summary>
        /// <returns></returns>
        private clsOrderStatus IsTaskOrderCompleteSuccess(FeedbackData feedbackData)
        {
            if (TaskOrder == null | SubTaskTracking == null)
            {
                return new clsOrderStatus
                {
                    Status = ORDER_STATUS.NO_ORDER
                };
            }
            if (TaskOrder.TaskName != feedbackData.TaskName)
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

            if (SubTaskTracking.Action == ACTION_TYPE.None) //處理移動任務的回報
            {
                //if (SubTaskTracking.SubPathPlan.Last() == SubTaskTracking.EntirePathPlan.Last())
                //{

                //}
                //var agv_currentMapPoint = SubTaskTracking.SubPathPlan[feedbackData.PointIndex];
                //var agv_currentMapPoint = SubTaskTracking.EntirePathPlan[feedbackData.PointIndex];
                if (SubTaskTracking.Destination.TagNumber != AGV.currentMapPoint.TagNumber)
                {
                    return new clsOrderStatus
                    {
                        Status = ORDER_STATUS.EXECUTING_WAITING,
                        AGVLocation = AGV.currentMapPoint
                    };
                }
            }

            if (orderACtion != ACTION_TYPE.Carry)
            {
                isOrderCompleted = previousCompleteAction == orderACtion;
            }
            else
                isOrderCompleted = previousCompleteAction == ACTION_TYPE.Load;
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

            var theta_error = CalculateThetaError(_destinTheta);
            if (Math.Abs(theta_error) > 10)
            {
                message = $"{AGV.Name} 角度與目的地[{destine_tag}]角度設定誤差>10度({AGV.states.Coordination.Theta}/{_destinTheta})";
                LOG.WARN(message);
                return false;
            }

            return true;
        }
        private double CalculateThetaError(double _destinTheta)
        {
            var _agvTheta = AGV.states.Coordination.Theta;
            var theta_error = Math.Abs(_agvTheta - _destinTheta);
            theta_error = theta_error > 180 ? 360 - theta_error : theta_error;
            return theta_error;
        }

        public async Task<SimpleRequestResponse> PostTaskCancelRequestToAGVAsync(RESET_MODE mode)
        {
            try
            {
                if (SubTaskTracking == null)
                    return new SimpleRequestResponse { ReturnCode = RETURN_CODE.OK };
                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = mode,
                    Task_Name = OrderTaskName,
                    TimeStamp = DateTime.Now,
                };
                SimpleRequestResponse taskStateResponse = await AGVHttp.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"/api/TaskDispatch/Cancel", reset_cmd);
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

        public TaskDownloadRequestResponse PostTaskRequestToAGVAsync(out clsSubTask task, bool isMovingSeqmentTask = false)
        {
            clsSubTask _task = null;
            task = null;
            try
            {
                _task = !isMovingSeqmentTask ? SubTasks.Dequeue() : SubTaskTracking;
                task = _task;

                if (_task.Action == ACTION_TYPE.None && !isMovingSeqmentTask)
                    _task.Source = AGV.currentMapPoint;

                var taskSeq = isMovingSeqmentTask ? _task.DownloadData.Task_Sequence + 1 : taskSequence;
                _task.CreateTaskToAGV(TaskOrder, taskSeq, out bool isSegmentTaskCreated, out clsMapPoint lastPt, isMovingSeqmentTask, AGV.states.Last_Visited_Node, AGV.states.Coordination.Theta);
                if (isSegmentTaskCreated)
                {
                    Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            waitingInfo.IsWaiting = true;
                            waitingInfo.WaitingPoint = _task.GetNextPointToGo(lastPt);
                            waitingInfo.Descrption = $"前往-{lastPt.Point_ID} 等待-{waitingInfo.WaitingPoint.TagNumber}可通行";
                            //WaitingRegistReleaseAndGo();
                        }
                        catch (Exception ex)
                        {
                        }

                    });
                }
                bool IsAGVAlreadyAtFinalPointOfTrajectory = _task.DownloadData.ExecutingTrajecory.Last().Point_ID == AGV.currentMapPoint.TagNumber && Math.Abs(CalculateThetaError(_task.DownloadData.ExecutingTrajecory.Last().Theta)) < 5;
                if (IsAGVAlreadyAtFinalPointOfTrajectory)
                    return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE };

                AGV.CheckAGVStatesBeforeDispatchTask(_task.Action, _task.Destination);
                if (AGV.options.Simulation)
                {
                    TaskDownloadRequestResponse taskStateResponse = AgvSimulation.ActionRequestHandler(_task.DownloadData).Result;
                    return taskStateResponse;
                }
                else
                {
                    TaskDownloadRequestResponse taskStateResponse = AGVHttp.PostAsync<TaskDownloadRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", _task.DownloadData).Result;

                    return taskStateResponse;
                }
            }
            catch (IlleagalTaskDispatchException ex)
            {
                AbortOrder(TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL, ex.Alarm_Code.ToString());
                AlarmManagerCenter.AddAlarm(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: OrderTaskName, location: AGV.currentMapPoint.Name);
                return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL, Message = ex.Alarm_Code.ToString() };
            }
            catch (Exception ex)
            {
                LOG.Critical(ex);
                return new TaskDownloadRequestResponse
                {
                    ReturnCode = TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION,
                    Message = ex.Message
                };
            }
        }


        private void CompleteOrder()
        {

            UnRegistPointsRegisted();
            ChangeTaskStatus(TASK_RUN_STATUS.ACTION_FINISH);
            taskCancel.Cancel();
        }
        internal void AbortOrder(TASK_DOWNLOAD_RETURN_CODES agv_task_return_code, string message = "")
        {
            UnRegistPointsRegisted();
            taskCancel.Cancel();
            ChangeTaskStatus(TASK_RUN_STATUS.FAILURE, failure_reason: message == "" ? agv_task_return_code.ToString() : message);
            ALARMS alarm_code = ALARMS.AGV_STATUS_DOWN;
            switch (agv_task_return_code)
            {
                case TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN:
                    alarm_code = ALARMS.AGV_STATUS_DOWN;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_NOT_ON_TAG:
                    alarm_code = ALARMS.AGV_AT_UNKNON_TAG_LOCATION;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.WORKSTATION_NOT_SETTING_YET:
                    alarm_code = ALARMS.AGV_WORKSTATION_DATA_NOT_SETTING;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_BATTERY_LOW_LEVEL:

                    alarm_code = ALARMS.AGV_BATTERY_LOW_LEVEL;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_CANNOT_GO_TO_WORKSTATION_WITH_NORMAL_MOVE_ACTION:
                    alarm_code = ALARMS.CANNOT_DISPATCH_NORMAL_MOVE_TASK_WHEN_DESTINE_IS_WORKSTATION;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL:
                    alarm_code = ALARMS.CANNOT_DISPATCH_TASK_WITH_ILLEAGAL_STATUS;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION:
                    alarm_code = ALARMS.SYSTEM_ERROR;
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.NO_PATH_FOR_NAVIGATION:
                    alarm_code = ALARMS.TRAFFIC_BLOCKED_NO_PATH_FOR_NAVIGATOR;
                    break;
                default:
                    break;
            }
            AlarmManagerCenter.AddAlarm(alarm_code, ALARM_SOURCE.AGVS, ALARM_LEVEL.ALARM, Equipment_Name: AGV.Name, location: AGV.currentMapPoint.Name, OrderTaskName);
        }

        internal async void CancelOrder()
        {
            UnRegistPointsRegisted();
            await PostTaskCancelRequestToAGVAsync(RESET_MODE.CYCLE_STOP);
            taskCancel.Cancel();
            ChangeTaskStatus(TASK_RUN_STATUS.CANCEL);

        }
        private void UnRegistPointsRegisted()
        {
            //解除除了當前位置知所有註冊點
            var allRegistedPoints = StaMap.Map.Points.Values.Where(pt => pt.RegistInfo != null).Where(pt => pt.RegistInfo.RegisterAGVName == AGV.Name);
            foreach (var pt in allRegistedPoints.Where(pt => pt.TagNumber != AGV.states.Last_Visited_Node))
            {
                if (!StaMap.UnRegistPoint(AGV.Name, pt, out var msg))
                {
                    LOG.WARN($"{AGV.Name}-交通解除註冊點{pt.TagNumber}失敗:{msg}");
                }
            }
            LOG.WARN($"{AGV.Name}-交通解除註冊點完成");
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

                using (var agvs = new AGVSDatabase())
                {
                    agvs.tables.Tasks.Update(TaskOrder);
                    agvs.tables.SaveChanges();
                }

                TaskOrder = null;
                _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;
            }
            else
            {

                using (var agvs = new AGVSDatabase())
                {
                    agvs.tables.Tasks.Update(TaskOrder);
                    agvs.tables.SaveChanges();
                }
            }
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

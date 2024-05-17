//#define CancelTaskTest
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.DATABASE.Helpers;
using AGVSystemCommonNet6.Exceptions;
using AGVSystemCommonNet6.HttpTools;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Microservices.AGVS;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Newtonsoft.Json;
using RosSharp.RosBridgeClient;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Timers;
using VMSystem.AGV.TaskDispatch.Tasks;

using VMSystem.TrafficControl;
using VMSystem.VMS;
using static AGVSystemCommonNet6.clsEnums;
using static AGVSystemCommonNet6.MAP.MapPoint;
using static AGVSystemCommonNet6.MAP.PathFinder;
using static VMSystem.AGV.TaskDispatch.clsAGVTaskTrack;

namespace VMSystem.AGV.TaskDispatch
{
    /// <summary>
    /// 追蹤AGV任務鍊
    /// </summary>
    public class clsAGVTaskTrack : clsTaskDatabaseWriteableAbstract, IDisposable
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

        public delegate TASK_RUN_STATUS QueryTaskOrderStateDelegate(string taskName);
        public QueryTaskOrderStateDelegate OnTaskOrderStatusQuery;

        public List<int> RemainTags
        {
            get
            {
                try
                {

                    if (TaskOrder == null || SubTaskTracking == null || TaskOrder.State != TASK_RUN_STATUS.NAVIGATING)
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
        public VehicleMovementStage _transferProcess = VehicleMovementStage.Not_Start_Yet;
        public VehicleMovementStage transferProcess
        {
            get => _transferProcess;
            set
            {
                if (_transferProcess != value)
                {
                    _transferProcess = value;
                    LOG.TRACE($"{AGV.Name} Transfer Process changed to {value}!", color: ConsoleColor.Yellow);
                }
            }
        }
        public ACTION_TYPE currentActionType { get; private set; } = ACTION_TYPE.Unknown;
        public ACTION_TYPE nextActionType { get; private set; } = ACTION_TYPE.Unknown;
        private CancellationTokenSource taskCancel = new CancellationTokenSource();
        private TASK_RUN_STATUS _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;

        public clsAGVSimulation AgvSimulation = null;

        public DateTime NextDestineETA { get; private set; } = DateTime.MaxValue;
        public TASK_RUN_STATUS TaskRunningStatus
        {
            get => _TaskRunningStatus;
            set
            {
                if (_TaskRunningStatus != value)
                {
                    _TaskRunningStatus = value;
                    if (_TaskRunningStatus == TASK_RUN_STATUS.CANCEL | _TaskRunningStatus == TASK_RUN_STATUS.FAILURE)
                    {
                        CancelOrder();
                    }
                }
            }
        }


        HttpHelper AGVHttp;
        HttpHelper AGVSAPIHttp;
        public clsAGVTaskTrack(clsAGVTaskDisaptchModule DispatchModule = null)
        {
            StartTaskStatusWatchDog();
        }

        private void StartTaskStatusWatchDog()
        {
            Task.Run(async () =>
            {
                MAIN_STATUS agv_status = MAIN_STATUS.Unknown;
                while (!disposedValue)
                {
                    Thread.Sleep(1);
                    if (TaskOrder == null || TaskOrder.State != TASK_RUN_STATUS.NAVIGATING)
                        continue;
                    TaskRunningStatus = OnTaskOrderStatusQuery(OrderTaskName);

                }
            });
        }
        public Queue<clsSubTask> SubTasks = new Queue<clsSubTask>();

        public Stack<clsSubTask> CompletedSubTasks = new Stack<clsSubTask>();

        public clsSubTask SubTaskTracking;

        public bool IsResumeTransferTask { get; private set; } = false;
        public bool WaitingForResume { get; private set; }

        public async Task Start(IAGV AGV, clsTaskDto TaskOrder, bool IsResumeTransferTask = false, VehicleMovementStage lastTransferProcess = default)
        {
            AgvSimulation = AGV.AgvSimulation;
            if (TaskOrder == null)
                return;
            this.IsResumeTransferTask = IsResumeTransferTask;
            this.transferProcess = lastTransferProcess;

            if (TaskOrder.Action == ACTION_TYPE.Carry && TaskOrder.From_Station == AGV.states.Last_Visited_Node + "")
            {
                this.IsResumeTransferTask = true;
                this.transferProcess = VehicleMovementStage.Traveling_To_Destine;
            }

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
                    waitingInfo.SetStatusNoWaiting(AGV);
                    WaitingForResume = false;
                    SubTasks = CreateSubTaskLinks(TaskOrder);
                    CompletedSubTasks = new Stack<clsSubTask>();
                    StartExecuteOrder();
                    StartRecordTrjectory();
                    LOG.INFO($"{AGV.Name}- {TaskOrder.Action} 訂單開始,動作:{string.Join("->", TrackingActions)}");
                }
                catch (IlleagalTaskDispatchException ex)
                {
                    AlarmManagerCenter.AddAlarmAsync(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: OrderTaskName, location: AGV.currentMapPoint.Name);
                }
            });

        }

        private void StartExecuteOrder()
        {
            UpdateTaskStartPointAndTime();
            taskSequence = 0;
            DownloadTaskToAGV();
        }

        private async void UpdateTaskStartPointAndTime()
        {
            try
            {
                TaskOrder.StartTime = DateTime.Now;
                if (TaskOrder.Action != ACTION_TYPE.Carry)
                    TaskOrder.From_Station = AGV.currentMapPoint.TagNumber + "";
                RaiseTaskDtoChange(this, TaskOrder);

            }
            catch (Exception ex)
            {

            }

        }

        private async void DownloadTaskToAGV(bool isMovingSeqmentTask = false)
        {
            if (TaskOrder == null)
                return;

            if (SubTasks.Count == 0 && !isMovingSeqmentTask)
            {
                await AlarmManagerCenter.AddAlarmAsync(ALARMS.SubTask_Queue_Empty_But_Try_DownloadTask_To_AGV);
                return;
            }
            TASK_DOWNLOAD_RETURN_CODES _return_code = default;

            _return_code = CalculationOptimizedPath(out clsSubTask _task, isMovingSeqmentTask).ReturnCode;

            if (_return_code == TASK_DOWNLOAD_RETURN_CODES.OK)
            {

                if (_task.Action == ACTION_TYPE.Load || _task.Action == ACTION_TYPE.Unload)
                    await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionStartReport(_task.Destination.TagNumber, _task.Action);

                if (IsStartMoveToDestineStationAfterUnload(_task))
                {
                    await AGVSSerivces.TRANSFER_TASK.StartTransferCargoReport(AGV.Name, TaskOrder.From_Station_Tag, TaskOrder.To_Station_Tag);
                }



                SubTaskTracking = _task;
                UpdateTransferProcess(TaskOrder.Action, SubTaskTracking.Action);

                TaskOrder.StartTime = DateTime.Now;
                RaiseTaskDtoChange(this, TaskOrder);

                _return_code = _DispatchTaskToAGV(_task, out ALARMS alarm).ReturnCode;
                if (_return_code != TASK_DOWNLOAD_RETURN_CODES.OK)
                {
                    AbortOrder(_return_code, alarm);
                    return;
                }
                else if (_return_code == TASK_DOWNLOAD_RETURN_CODES.OK)
                {
                    // RegistRemainPathTags();
                }
            }
            else
            {
                LOG.Critical($"CalculationOptimizedPath => {_return_code}");
            }

            bool IsStartMoveToDestineStationAfterUnload(clsSubTask _task)
            {
                return TaskOrder.Action == ACTION_TYPE.Carry && _task.Action == ACTION_TYPE.None && previousCompleteAction == ACTION_TYPE.Unload;
            }
        }


        private void UpdateTransferProcess(ACTION_TYPE order_action, ACTION_TYPE sub_action)
        {
            if (order_action != ACTION_TYPE.Carry)
            {
                if (sub_action == ACTION_TYPE.None)
                {
                    if (order_action == ACTION_TYPE.Charge)
                        transferProcess = VehicleMovementStage.Traveling;
                    else if (order_action == ACTION_TYPE.Load || order_action == ACTION_TYPE.Unload)
                        transferProcess = VehicleMovementStage.Traveling_To_Destine;
                    else
                        transferProcess = VehicleMovementStage.Traveling;
                }
                else if (sub_action == ACTION_TYPE.Load)
                    transferProcess = VehicleMovementStage.WorkingAtDestination;

                else if (sub_action == ACTION_TYPE.Unload)
                    transferProcess = VehicleMovementStage.WorkingAtSource;

                else if (sub_action == ACTION_TYPE.Charge)
                    transferProcess = VehicleMovementStage.WorkingAtChargeStation;
                else if (sub_action == ACTION_TYPE.Discharge || sub_action == ACTION_TYPE.Unpark)
                    transferProcess = VehicleMovementStage.LeaveFrom_WorkStation;
            }
            else
            {
                if (previousCompleteAction == ACTION_TYPE.Unknown | previousCompleteAction == ACTION_TYPE.Discharge | previousCompleteAction == ACTION_TYPE.Unpark)
                    transferProcess = VehicleMovementStage.Traveling_To_Source;
                else if (previousCompleteAction == ACTION_TYPE.Load)
                    transferProcess = VehicleMovementStage.Traveling_To_Destine;
                else
                    transferProcess = VehicleMovementStage.Traveling_To_Destine;
            }
        }

        private void RegistRemainPathTags()
        {
            var current_point_index = SubTaskTracking.EntirePathPlan.FindIndex(pt => pt.TagNumber == AGV.currentMapPoint.TagNumber);
            MapPoint[] path_gen = new MapPoint[SubTaskTracking.EntirePathPlan.Count - current_point_index];
            Array.Copy(SubTaskTracking.EntirePathPlan.ToArray(), 0, path_gen, 0, path_gen.Length);
            var tags = string.Join(",", path_gen.Select(pt => pt.TagNumber));
            if (StaMap.RegistPoint(AGV.Name, path_gen, out string msg))
            {
                LOG.TRACE($"{AGV.Name} Regist {tags}");
            }
            else
            {

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
                    if (workstation_point.StationType == STATION_TYPE.STK || workstation_point.StationType == STATION_TYPE.Charge_STK)
                    {
                        subTask_load.Action = ACTION_TYPE.LoadAndPark;
                    }
                    task_links.Enqueue(subTask_load);
                }

            }

            if (IsResumeTransferTask)
            {
                var taskLinkList = task_links.ToList();
                var removeout = taskLinkList.FirstOrDefault(tk => tk.Action == ACTION_TYPE.Unpark | tk.Action == ACTION_TYPE.Discharge);
                if (removeout != null)
                {
                    taskLinkList.Remove(removeout);
                }
                if (transferProcess == VehicleMovementStage.Traveling_To_Source)
                {
                    previousCompleteAction = ACTION_TYPE.None;
                }
                else
                {
                    previousCompleteAction = ACTION_TYPE.Unload;
                    removeout = taskLinkList.FirstOrDefault(tk => tk.Action == ACTION_TYPE.None); //移除第一段跑貨移動任務
                    if (removeout != null)
                    {
                        taskLinkList.Remove(removeout);
                    }
                    removeout = taskLinkList.FirstOrDefault(tk => tk.Action == ACTION_TYPE.Unload); //移除第一段取貨移動任務
                    if (removeout != null)
                    {
                        taskLinkList.Remove(removeout);
                    }
                }
                task_links.Clear();
                foreach (var task in taskLinkList)
                {
                    task_links.Enqueue(task);
                }
            }

            return task_links;
        }
        public class clsOdometryRecord
        {
            public DateTime time { get; private set; } = DateTime.MaxValue;
            public double odometry { get; private set; } = 0;
            public void Update(DateTime time, double odometry)
            {
                this.time = time;
                this.odometry = odometry;
            }
        }
        private clsOdometryRecord _previousOdomeryRecord = new clsOdometryRecord();
        /// <summary>
        /// 處理AGV任務回報
        /// </summary>
        /// <param name="feedbackData"></param>
        public async Task<TASK_FEEDBACK_STATUS_CODE> HandleAGVFeedback(FeedbackData feedbackData)
        {

            var task_simplex = feedbackData.TaskSimplex;
            var task_status = feedbackData.TaskStatus;
            var agv_current_tag = AGV.currentMapPoint.TagNumber;
            LOG.INFO($"{AGV.Name} Feedback Task Status:{feedbackData.ToJson()}|at tag-{agv_current_tag}", color: ConsoleColor.Green);
            if (AGV.main_state == MAIN_STATUS.DOWN)
            {
                taskCancel.Cancel();
                //_ = PostTaskCancelRequestToAGVAsync(RESET_MODE.ABORT);
                //嘗試抓取車載回報的異常碼
                string agv_alarm = "";
                if (AGV.states.Alarm_Code.Any())
                {
                    agv_alarm = string.Join(",", AGV.states.Alarm_Code.Select(alarm => alarm.FullDescription));
                }
                LOG.ERROR($"{AGV.Name}-State DOWN When Task Action Finish Reported-ALARM:{agv_alarm}");
                AbortOrder(TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN, ALARMS.AGV_STATUS_DOWN, agv_alarm);
                return TASK_FEEDBACK_STATUS_CODE.OK;
            }
            switch (task_status)
            {
                case TASK_RUN_STATUS.NO_MISSION:
                    break;
                case TASK_RUN_STATUS.NAVIGATING:
                    NextDestineETA = EstimatedArrivalTime(AGV.states.Odometry);
                    StoreOdomerty(AGV.states.Odometry);

                    LOG.INFO($"{AGV.Name} 預估抵達 {SubTaskTracking.Destination.Graph.Display}(Tag-{SubTaskTracking.Destination.TagNumber}) 時間={NextDestineETA}");
                    break;
                case TASK_RUN_STATUS.REACH_POINT_OF_TRAJECTORY:
                    break;
                case TASK_RUN_STATUS.ACTION_START:

                    NextDestineETA = EstimatedArrivalTime(AGV.states.Odometry);
                    StoreOdomerty(AGV.states.Odometry);

                    LOG.INFO($"{AGV.Name} Feedback Task Action Start[{SubTaskTracking.Action}]");
                    if (SubTaskTracking.Action == ACTION_TYPE.Unload)
                        transferProcess = VehicleMovementStage.WorkingAtSource;
                    else if (SubTaskTracking.Action == ACTION_TYPE.Load)
                        transferProcess = VehicleMovementStage.WorkingAtDestination;
                    break;
                case TASK_RUN_STATUS.ACTION_FINISH:
                    TryNotifyToAGVSLDULDActionFinish();
                    var orderStatus = IsTaskOrderCompleteSuccess(feedbackData);
                    if (orderStatus.Status == ORDER_STATUS.COMPLETED | orderStatus.Status == ORDER_STATUS.NO_ORDER)
                    {
                        CompletedSubTasks.Push(SubTaskTracking);
                        transferProcess = VehicleMovementStage.Completed;
                        CompleteOrder();
                        return TASK_FEEDBACK_STATUS_CODE.OK;
                    }
                    else if (orderStatus.Status == ORDER_STATUS.EXECUTING_WAITING)
                    {
                        _ = Task.Factory.StartNew(async () =>
                        {
                            try
                            {
                                var LastPoint = StaMap.GetPointByTagNumber(AGV.states.Last_Visited_Node);
                                waitingInfo.SetStatusWaitingConflictPointRelease(AGV, AGV.states.Last_Visited_Node, SubTaskTracking.GetNextPointToGo(SubTaskTracking.SubPathPlan.Last(), false));
                                waitingInfo.AllowMoveResumeResetEvent.WaitOne();
                                waitingInfo.SetStatusNoWaiting(AGV);
                                if (!AGV.IsSolvingTrafficInterLock)
                                {
                                    DownloadTaskToAGV(true);
                                }
                            }
                            catch (Exception ex)
                            {
                            }
                        });
                        return TASK_FEEDBACK_STATUS_CODE.OK;
                    }
                    else if (orderStatus.Status == ORDER_STATUS.FAILURE)
                    {
                        _ = Task.Factory.StartNew(async () =>
                        {
                            await Task.Delay(2000);
                            await PostTaskCancelRequestToAGVAsync(RESET_MODE.CYCLE_STOP);
                        });
                        CompletedSubTasks.Push(SubTaskTracking);
                        transferProcess = VehicleMovementStage.Completed;
                        AbortOrder(TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN, orderStatus.AlarmCode);
                        return TASK_FEEDBACK_STATUS_CODE.OK;
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

        private void StoreOdomerty(double odometry)
        {
            _previousOdomeryRecord.Update(DateTime.Now, odometry);
        }

        /// <summary>
        /// 預估到達目的地的時間
        /// </summary>
        /// <param name="currentOdometry"></param>
        private DateTime EstimatedArrivalTime(double currentOdometry)
        {
            try
            {
                DateTime _time = DateTime.Now;
                double _period = (_time - _previousOdomeryRecord.time).TotalSeconds;
                double _distance_changed = currentOdometry - _previousOdomeryRecord.odometry;
                if (_period < 0)
                {
                    return DateTime.Now;
                }
                else
                {
                    LOG.INFO($"{AGV.Name} 移動-{_distance_changed} km,花費 /{_period}秒");
                    double speed = _distance_changed / _period;
                    List<MapPoint> _remain_path_points = RemainTags.Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
                    var _remain_path_points_extended = _remain_path_points.ToList();
                    _remain_path_points_extended.Add(_remain_path_points.Last());

                    double _remain_distance = 0.001 * _remain_path_points.Select(pt => pt.CalculateDistance(_remain_path_points_extended[_remain_path_points.IndexOf(pt) + 1])).Sum();
                    double time_sec_estimate = _remain_distance / speed; //v=s/t==> t = s/v
                    DateTime arrivalTimeEstimated = DateTime.Now.AddSeconds(time_sec_estimate);
                    return arrivalTimeEstimated;
                }
            }
            catch (Exception ex)
            {
                LOG.ERROR($"預估抵達目的地時間計算的過程中發生錯誤:{ex.Message}", ex);
                return DateTime.Now;
            }

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
            public ALARMS AlarmCode = ALARMS.NONE;
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
                var agv_currentMapPoint = SubTaskTracking.EntirePathPlan[feedbackData.PointIndex];
                if (SubTaskTracking.Destination.TagNumber != agv_currentMapPoint.TagNumber)
                {
                    return new clsOrderStatus
                    {
                        Status = ORDER_STATUS.EXECUTING_WAITING,
                        AGVLocation = agv_currentMapPoint
                    };
                }
            }

            if (previousCompleteAction == ACTION_TYPE.Unload && TaskOrder.Carrier_ID != "")
            {
                string cst_repoted = AGV.states.CSTID.First();
                bool cst_exist = AGV.states.Cargo_Status == 1;
                string agv_loc = AGV.currentMapPoint.Name;
                string task_name = TaskOrder.TaskName;
                if (!cst_exist)
                {
                    return new clsOrderStatus
                    {
                        Status = ORDER_STATUS.FAILURE,
                        FailureReason = $"Unload Done But AGV No Cargo Mounted.",
                        AlarmCode = ALARMS.UNLOAD_BUT_AGV_NO_CARGO_MOUNTED
                    };
                }
                else
                {
                    if (cst_repoted == "")
                    {
                        return new clsOrderStatus
                        {
                            Status = ORDER_STATUS.FAILURE,
                            FailureReason = $"Unload Done But AGV Report Empty Cargo ID",
                            AlarmCode = ALARMS.UNLOAD_BUT_CARGO_ID_EMPTY
                        };
                    }
                    else if (cst_repoted != TaskOrder.Carrier_ID)
                    {
                        return new clsOrderStatus
                        {
                            Status = ORDER_STATUS.FAILURE,
                            FailureReason = $"Unload Done But AGV Cargo ID Not Match",
                            AlarmCode = ALARMS.UNLOAD_BUT_CARGO_ID_NOT_MATCHED
                        };
                    }
                }
            }

            if (orderACtion != ACTION_TYPE.Carry)
            {
                isOrderCompleted = previousCompleteAction == orderACtion;
            }
            else
            {
                isOrderCompleted = previousCompleteAction == ACTION_TYPE.Load | previousCompleteAction == ACTION_TYPE.LoadAndPark;
            }
            return new clsOrderStatus
            {
                Status = isOrderCompleted ? ORDER_STATUS.COMPLETED : ORDER_STATUS.EXECUTING
            };
        }
        /// <summary>
        /// 檢查AGV是否抵達終點且角度正確
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
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
                AgvSimulation?.CancelTask();
                if (SubTaskTracking == null)
                    return new SimpleRequestResponse { ReturnCode = RETURN_CODE.OK };
                clsCancelTaskCmd reset_cmd = new clsCancelTaskCmd()
                {
                    ResetMode = mode,
                    Task_Name = OrderTaskName,
                    TimeStamp = DateTime.Now,
                };
                SimpleRequestResponse taskStateResponse;
                if (AGV.options.Protocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
                {
                    taskStateResponse = await AGVHttp.PostAsync<SimpleRequestResponse, clsCancelTaskCmd>($"/api/TaskDispatch/Cancel", reset_cmd);
                }
                else
                {
                    taskStateResponse = AGV.TcpClientHandler.SendTaskCancelMessage(reset_cmd);
                }
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

        private static object RegistLockObject = new object();

        public TaskDownloadRequestResponse CalculationOptimizedPath(out clsSubTask task, bool isMovingSeqmentTask = false)
        {
            clsSubTask _task = null;
            task = null;
            try
            {
                _task = isMovingSeqmentTask ? SubTaskTracking : SubTasks.Dequeue();
                task = _task;

                if (_task.Action == ACTION_TYPE.None && !isMovingSeqmentTask)
                    _task.Source = AGV.currentMapPoint;

                if (_task.Action == ACTION_TYPE.Discharge || _task.Action == ACTION_TYPE.Unpark)
                {
                    WaitWorkStationSecondaryPointRelease(_task);
                    if (TaskOrder == null || TaskOrder.State != TASK_RUN_STATUS.NAVIGATING)
                        return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_CANCEL };
                }

                var taskSeq = isMovingSeqmentTask ? _task.DownloadData.Task_Sequence + 1 : taskSequence;

                lock (RegistLockObject)
                {
                    _task.GenOptimizePathOfTask(TaskOrder, taskSeq, out bool isSegmentTaskCreated, out clsMapPoint lastPt, isMovingSeqmentTask, AGV.states.Last_Visited_Node, AGV.states.Coordination.Theta);
                }
                if (!isMovingSeqmentTask)
                    SubTaskTracking = _task;

                return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.OK };
            }
            catch (IlleagalTaskDispatchException ex)
            {
                AbortOrder(TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL, ALARMS.TASK_DOWNLOAD_DATA_ILLEAGAL, ex.Alarm_Code.ToString());
                AlarmManagerCenter.AddAlarmAsync(ex.Alarm_Code, Equipment_Name: AGV.Name, taskName: OrderTaskName, location: AGV.currentMapPoint.Name);
                return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL, Message = ex.Alarm_Code.ToString() };
            }
            catch (Exception ex)
            {
                LOG.Critical(ex.StackTrace);
                return new TaskDownloadRequestResponse
                {
                    ReturnCode = TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION,
                    Message = ex.Message
                };
            }

        }
        TaskDownloadRequestResponse _DispatchTaskToAGV(clsSubTask _task, out ALARMS alarm)
        {
            alarm = ALARMS.NONE;
            if (AGV.options.Simulation)
            {
                TaskDownloadRequestResponse taskStateResponse = AgvSimulation.ExecuteTask(_task.DownloadData).Result;
                return taskStateResponse;
            }
            else
            {
                try
                {
                    AGV.CheckAGVStatesBeforeDispatchTask(_task.Action, _task.Destination);
                }
                catch (IlleagalTaskDispatchException ex)
                {
                    alarm = ex.Alarm_Code;
                    return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL };
                }
                try
                {
                    TaskDownloadRequestResponse taskStateResponse = new TaskDownloadRequestResponse();

                    if (AGV.options.Protocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
                        taskStateResponse = AGVHttp.PostAsync<TaskDownloadRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", _task.DownloadData).Result;
                    else
                        taskStateResponse = AGV.TcpClientHandler.SendTaskMessage(_task.DownloadData);

#if CancelTaskTest
                    Task.Factory.StartNew(async () =>
                    {
                        await Task.Delay(100);
                        this.CancelOrder();
                    });
#endif

                    return taskStateResponse;
                }
                catch (Exception)
                {
                    alarm = ALARMS.Download_Task_To_AGV_Fail;
                    return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL };
                }
            }
        }

        private void WaitWorkStationSecondaryPointRelease(clsSubTask task)
        {

            if (StaMap.GetPointRegisterName(task.Destination.TagNumber, out string agv))
            {
                if (agv != AGV.Name)
                {
                    StaMap.UnRegistPoint(AGV.Name, task.Destination.TagNumber);
                    waitingInfo.SetStatusWaitingConflictPointRelease(AGV, AGV.currentMapPoint.TagNumber, task.Destination);
                    waitingInfo.AllowMoveResumeResetEvent.WaitOne();
                    waitingInfo.SetStatusNoWaiting(AGV);
                    StaMap.RegistPoint(AGV.Name, task.Destination, out var msg);//重新註冊二次定位點
                }
            }

            var agv_distance_from_secondaryPt = VMSManager.AllAGV.FilterOutAGVFromCollection(this.AGV.Name).ToDictionary(agv => agv, agv => new MapPoint() { X = agv.states.Coordination.X, Y = agv.states.Coordination.Y }.CalculateDistance(task.Destination));
            var tooNearAgvDistanc = agv_distance_from_secondaryPt.Where(kp => kp.Value <= AGV.options.VehicleLength / 2.0 / 100.0);
            if (tooNearAgvDistanc.Any())
            {
                StaMap.UnRegistPoint(AGV.Name, task.Destination.TagNumber);
                foreach (var kp in tooNearAgvDistanc)
                {
                    waitingInfo.SetStatusWaitingConflictPointRelease(AGV, AGV.currentMapPoint.TagNumber, kp.Key.currentMapPoint);
                    waitingInfo.AllowMoveResumeResetEvent.WaitOne();
                    waitingInfo.SetStatusNoWaiting(AGV);
                }
                StaMap.RegistPoint(AGV.Name, task.Destination, out var msg); //重新註冊二次定位點
            }
        }

        private void CompleteOrder()
        {
            TryNotifyToAGVSLDULDActionFinish();
            LOG.ERROR($"Task_{OrderTaskName} Order COMPLETE");
            EndReocrdTrajectory();
            UnRegistPointsRegisted();
            ChangeTaskStatus(OrderTaskName, TASK_RUN_STATUS.ACTION_FINISH);
            taskCancel.Cancel();
            waitingInfo.AllowMoveResumeResetEvent.Set();
            AgvSimulation?.CancelTask();
        }

        private async void TryNotifyToAGVSLDULDActionFinish()
        {
            if (SubTaskTracking == null)
                return;
            var lastAction = SubTaskTracking.Action;
            if (lastAction != ACTION_TYPE.Unload && lastAction != ACTION_TYPE.Load)
                return;

            var lastEQTag = SubTaskTracking.Destination.TagNumber;
            _ = Task.Factory.StartNew(async () =>
            {
                await AGVSSerivces.TRANSFER_TASK.LoadUnloadActionFinishReport(lastEQTag, lastAction);
            });
        }

        internal void AbortOrder(TASK_DOWNLOAD_RETURN_CODES agv_task_return_code, ALARMS alarm_code = ALARMS.NONE, string message = "")
        {
            TryNotifyToAGVSLDULDActionFinish();
            LOG.ERROR($"Task_{OrderTaskName} Order ABORT");
            LOG.Critical(agv_task_return_code.ToString());
            UnRegistPointsRegisted();
            taskCancel.Cancel();
            waitingInfo.AllowMoveResumeResetEvent.Set();
            AgvSimulation?.CancelTask();

            if (agv_task_return_code == TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN && SystemModes.RunMode == AGVSystemCommonNet6.AGVDispatch.RunMode.RUN_MODE.RUN && TaskOrder.Action == ACTION_TYPE.Carry)
            {
                WaitingForResume = true;
                ChangeTaskStatus(OrderTaskName, TASK_RUN_STATUS.WAIT, failure_reason: message == "" ? alarm_code.ToString() : message);
            }
            else
            {
                WaitingForResume = false;
                ChangeTaskStatus(OrderTaskName, TASK_RUN_STATUS.FAILURE, failure_reason: message == "" ? alarm_code.ToString() : message);
                EndReocrdTrajectory();

            }
            if (alarm_code == ALARMS.NONE)
            {
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
            }
            AlarmManagerCenter.AddAlarmAsync(alarm_code, ALARM_SOURCE.AGVS, ALARM_LEVEL.ALARM, Equipment_Name: AGV.Name, location: AGV.currentMapPoint?.Name, OrderTaskName);
        }

        internal async Task<string> CancelOrder(bool unRegistPoints = true)
        {
            TryNotifyToAGVSLDULDActionFinish();
            LOG.ERROR($"Task_{OrderTaskName} Order CANCEL");
            EndReocrdTrajectory();
            await PostTaskCancelRequestToAGVAsync(RESET_MODE.CYCLE_STOP);
            taskCancel.Cancel();
            ChangeTaskStatus(OrderTaskName, TASK_RUN_STATUS.CANCEL);
            if (unRegistPoints)
                UnRegistPointsRegisted();
            return OrderTaskName;

        }
        private void UnRegistPointsRegisted()
        {
            //解除除了當前位置知所有註冊點
            var IsAllPointsUnRegisted = StaMap.UnRegistPointsOfAGVRegisted(AGV).GetAwaiter().GetResult();
            //Map.Points.Values.Where(pt => pt.RegistInfo != null).Where(pt => pt.RegistInfo.RegisterAGVName == AGV.Name);
            if (IsAllPointsUnRegisted)
            {
                LOG.WARN($"{AGV.Name}-交通解除註冊點完成");
            }
            else
            {
                LOG.WARN($"{AGV.Name}-交通解除註冊點失敗");
            }

        }
        internal async void ChangeTaskStatus(string TaskName, TASK_RUN_STATUS status, string failure_reason = "")
        {
            if (TaskOrder == null)
                return;
            TaskOrder.State = status;
            if (status == TASK_RUN_STATUS.FAILURE | status == TASK_RUN_STATUS.CANCEL | status == TASK_RUN_STATUS.ACTION_FINISH | status == TASK_RUN_STATUS.WAIT)
            {
                waitingInfo.AllowMoveResumeResetEvent.Set();
                waitingInfo.SetStatusNoWaiting(AGV);
                TaskOrder.FailureReason = failure_reason;
                TaskOrder.FinishTime = DateTime.Now;
                using (var agvs = new AGVSDatabase())
                {
                    var existFailureReason = agvs.tables.Tasks.AsNoTracking().FirstOrDefault(task => task.TaskName == TaskName).FailureReason;
                    if (existFailureReason != "")
                        TaskOrder.FailureReason = existFailureReason;
                    RaiseTaskDtoChange(this, TaskOrder);
                }
                _TaskRunningStatus = TASK_RUN_STATUS.NO_MISSION;
            }
            else
            {
                RaiseTaskDtoChange(this, TaskOrder);
            }
        }
        System.Timers.Timer? TrajectoryStoreTimer;
        private bool disposedValue;

        private void StartRecordTrjectory()
        {
            TrajectoryStoreTimer = new System.Timers.Timer()
            {
                Interval = 1000
            };
            TrajectoryStoreTimer.Elapsed += TrajectoryStoreTimer_Elapsed;
            TrajectoryStoreTimer.Enabled = true;
        }
        public void EndReocrdTrajectory()
        {
            TrajectoryStoreTimer?.Stop();
            TrajectoryStoreTimer?.Dispose();
            LOG.WARN($"{AGV.Name} End Store trajectory of Task-{OrderTaskName}");
        }

        /// <summary>
        /// 儲存軌跡到資料庫
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TrajectoryStoreTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            await StoreTrajectory();
        }
        private async Task StoreTrajectory()
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
            var result = await helper.StoreTrajectory(taskID, agvName, x, y, theta);
            if (!result.success)
            {
                LOG.ERROR($"[{AGV.Name}] trajectory store of task {OrderTaskName} DB ERROR : {result.error_msg}");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 處置受控狀態 (受控物件)
                }
                AGVHttp?.Dispose();
                EndReocrdTrajectory();
                disposedValue = true;
            }
        }

        // // TODO: 僅有當 'Dispose(bool disposing)' 具有會釋出非受控資源的程式碼時，才覆寫完成項
        // ~clsAGVTaskTrack()
        // {
        //     // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 請勿變更此程式碼。請將清除程式碼放入 'Dispose(bool disposing)' 方法
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }


}

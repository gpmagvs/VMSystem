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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Newtonsoft.Json;
using RosSharp.RosBridgeClient;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        public TRANSFER_PROCESS _transferProcess = TRANSFER_PROCESS.NOT_START_YET;
        public TRANSFER_PROCESS transferProcess
        {
            get => _transferProcess;
            set
            {
                if (_transferProcess != value)
                {
                    _transferProcess = value;
                    LOG.TRACE($"{AGV.Name} Transfer Process changed to {value}!");
                }
            }
        }
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
                    if (_TaskRunningStatus == TASK_RUN_STATUS.CANCEL | _TaskRunningStatus == TASK_RUN_STATUS.FAILURE)
                    {
                        CancelOrder();
                    }
                }
            }
        }


        HttpHelper AGVHttp;

        public clsAGVTaskTrack(clsAGVTaskDisaptchModule DispatchModule = null)
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

                    TaskRunningStatus = await TaskDBHelper.GetTaskStateByID(OrderTaskName);

                }
            });
        }
        public Queue<clsSubTask> SubTasks = new Queue<clsSubTask>();

        public Stack<clsSubTask> CompletedSubTasks = new Stack<clsSubTask>();

        public clsSubTask SubTaskTracking;

        public bool IsResumeTransferTask { get; private set; } = false;
        public bool WaitingForResume { get; private set; }

        public async Task Start(IAGV AGV, clsTaskDto TaskOrder, bool IsResumeTransferTask = false, TRANSFER_PROCESS lastTransferProcess = default)
        {
            AgvSimulation = AGV.AgvSimulation;
            if (TaskOrder == null)
                return;
            this.IsResumeTransferTask = IsResumeTransferTask;
            this.transferProcess = lastTransferProcess;
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
                    TaskOrder.From_Station = AGV.currentMapPoint.Name;
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
            TASK_DOWNLOAD_RETURN_CODES agv_task_return_code = default;

            agv_task_return_code = CalculationOptimizedPathAndSendTaskToAGV(out var _task, isMovingSeqmentTask).ReturnCode;
            if (agv_task_return_code != TASK_DOWNLOAD_RETURN_CODES.OK && agv_task_return_code != TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE)
            {
                AbortOrder(agv_task_return_code);
                return;
            }
            else if (agv_task_return_code == TASK_DOWNLOAD_RETURN_CODES.OK)
            {
              // RegistRemainPathTags();
            }
            SubTaskTracking = _task;

            if (agv_task_return_code == TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE)
            {
                LOG.INFO($"AGV Already locate in end of trajectory!");
                await HandleAGVFeedback(new FeedbackData
                {
                    TaskName = SubTaskTracking.DownloadData.Task_Name,
                    TaskSimplex = SubTaskTracking.DownloadData.Task_Simplex,
                    TaskStatus = TASK_RUN_STATUS.ACTION_FINISH,
                });
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
                if (transferProcess == TRANSFER_PROCESS.GO_TO_SOURCE_EQ)
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

                //嘗試抓取車載回報的異常碼

                string agv_alarm = "";
                if (AGV.states.Alarm_Code.Any())
                {
                    agv_alarm = string.Join(",", AGV.states.Alarm_Code.Select(alarm => alarm.FullDescription));
                }

                AbortOrder(TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN, ALARMS.AGV_STATUS_DOWN, agv_alarm);
                return TASK_FEEDBACK_STATUS_CODE.OK;
            }
            switch (task_status)
            {
                case TASK_RUN_STATUS.NO_MISSION:
                    break;
                case TASK_RUN_STATUS.NAVIGATING:
                    if (previousCompleteAction == ACTION_TYPE.Unknown | previousCompleteAction == ACTION_TYPE.Discharge | previousCompleteAction == ACTION_TYPE.Unpark)
                        transferProcess = TRANSFER_PROCESS.GO_TO_SOURCE_EQ;
                    else if (previousCompleteAction == ACTION_TYPE.Load)
                        transferProcess = TRANSFER_PROCESS.GO_TO_DESTINE_EQ;
                    else
                        transferProcess = TRANSFER_PROCESS.GO_TO_DESTINE_EQ;
                    break;

                case TASK_RUN_STATUS.REACH_POINT_OF_TRAJECTORY:
                    break;
                case TASK_RUN_STATUS.ACTION_START:
                    if (SubTaskTracking.Action == ACTION_TYPE.Unload)
                        transferProcess = TRANSFER_PROCESS.WORKING_AT_SOURCE_EQ;
                    else if (SubTaskTracking.Action == ACTION_TYPE.Load)
                        transferProcess = TRANSFER_PROCESS.WORKING_AT_DESTINE_EQ;
                    break;
                case TASK_RUN_STATUS.ACTION_FINISH:
                    var orderStatus = IsTaskOrderCompleteSuccess(feedbackData);
                    if (orderStatus.Status == ORDER_STATUS.COMPLETED | orderStatus.Status == ORDER_STATUS.NO_ORDER)
                    {
                        CompletedSubTasks.Push(SubTaskTracking);
                        transferProcess = TRANSFER_PROCESS.FINISH;
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
                                waitingInfo.SetStatusWaitingConflictPointRelease(AGV, AGV.states.Last_Visited_Node, SubTaskTracking.GetNextPointToGo(SubTaskTracking.SubPathPlan.Last(), true));
                                waitingInfo.AllowMoveResumeResetEvent.WaitOne();
                                waitingInfo.SetStatusNoWaiting(AGV);
                                DownloadTaskToAGV(true);
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
                        transferProcess = TRANSFER_PROCESS.FINISH;
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

        private async Task<bool> WaitingRegistReleaseAndGo()
        {
            return await Task.Factory.StartNew(() =>
              {
                  while (StaMap.IsMapPointRegisted(waitingInfo.WaitingPoint, AGV.Name))
                  {
                      Thread.Sleep(1000);
                      if (taskCancel.IsCancellationRequested)
                      {
                          LOG.INFO($"任務已取消結束等待");
                          return false;
                      }
                  }
                  if (taskCancel.IsCancellationRequested)
                  {
                      LOG.INFO($"任務已取消結束等待");
                      return false;
                  }
                  LOG.INFO($"{waitingInfo.WaitingPoint.Name}已解除註冊,任務下發");
                  waitingInfo.SetStatusNoWaiting(AGV);
                  DownloadTaskToAGV(true);
                  return true;
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
                isOrderCompleted = previousCompleteAction == ACTION_TYPE.Load;
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
                AgvSimulation.CancelTask();
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

        private static object RegistLockObject = new object();

        public TaskDownloadRequestResponse CalculationOptimizedPathAndSendTaskToAGV(out clsSubTask task, bool isMovingSeqmentTask = false)
        {
            clsSubTask _task = null;
            task = null;
            try
            {
                _task = isMovingSeqmentTask ? SubTaskTracking : SubTasks.Dequeue();
                task = _task;



                if (_task.Action == ACTION_TYPE.None && !isMovingSeqmentTask)
                    _task.Source = AGV.currentMapPoint;

                var agv_too_near_from_path = VMSManager.GetAGVListExpectSpeficAGV(AGV.Name).Where(_agv => _task.EntirePathPlan.Any(pt => pt.CalculateDistance(_agv.states.Coordination.X, _agv.states.Coordination.Y) * 100.0 <= _agv.options.VehicleLength));
                var desineRegistInfo = _task.Destination.RegistInfo == null ? new clsPointRegistInfo() : _task.Destination.RegistInfo;

                if (StaMap.IsMapPointRegisted(_task.Destination, AGV.Name) | agv_too_near_from_path.Any())
                {
                    if (_task.Action == ACTION_TYPE.Unpark | _task.Action == ACTION_TYPE.Discharge)
                    {
                        var nextPt = task.Destination;
                        waitingInfo.SetStatusWaitingConflictPointRelease(AGV, AGV.states.Last_Visited_Node, nextPt);
                        waitingInfo.AllowMoveResumeResetEvent.WaitOne();
                        waitingInfo.SetStatusNoWaiting(AGV);
                    }
                    else if (_task.Action != ACTION_TYPE.None)
                    {
                        if (VMSManager.AllAGV.Any(agv => agv.currentMapPoint.TagNumber == _task.Destination.TagNumber))
                            AlarmManagerCenter.AddAlarmAsync(ALARMS.Destine_EQ_Has_AGV);
                        else
                            AlarmManagerCenter.AddAlarmAsync(ALARMS.Destine_EQ_Has_Registed);

                        return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.NO_PATH_FOR_NAVIGATION };
                    }
                }
                var taskSeq = isMovingSeqmentTask ? _task.DownloadData.Task_Sequence + 1 : taskSequence;
                lock (RegistLockObject)
                {
                    _task.GenOptimizePathOfTask(TaskOrder, taskSeq, out bool isSegmentTaskCreated, out clsMapPoint lastPt, isMovingSeqmentTask, AGV.states.Last_Visited_Node, AGV.states.Coordination.Theta);
                }
                if (!isMovingSeqmentTask)
                    SubTaskTracking = _task;
                return _DispatchTaskToAGV(_task);
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

            TaskDownloadRequestResponse _DispatchTaskToAGV(clsSubTask _task)
            {
                bool IsAGVAlreadyAtFinalPointOfTrajectory = _task.DownloadData.ExecutingTrajecory.Last().Point_ID == AGV.currentMapPoint.TagNumber && Math.Abs(CalculateThetaError(_task.DownloadData.ExecutingTrajecory.Last().Theta)) < 5;
                if (IsAGVAlreadyAtFinalPointOfTrajectory)
                    return new TaskDownloadRequestResponse { ReturnCode = TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE };

                if (AGV.options.Simulation)
                {
                    TaskDownloadRequestResponse taskStateResponse = AgvSimulation.ActionRequestHandler(_task.DownloadData).Result;
                    return taskStateResponse;
                }
                else
                {
                    AGV.CheckAGVStatesBeforeDispatchTask(_task.Action, _task.Destination);
                    TaskDownloadRequestResponse taskStateResponse = new TaskDownloadRequestResponse();
                    if (AGV.options.Protocol == AGVSystemCommonNet6.Microservices.VMS.clsAGVOptions.PROTOCOL.RESTFulAPI)
                        taskStateResponse = AGVHttp.PostAsync<TaskDownloadRequestResponse, clsTaskDownloadData>($"/api/TaskDispatch/Execute", _task.DownloadData).Result;
                    else
                    {
                        taskStateResponse = AGV.TcpClientHandler.SendTaskMessage(_task.DownloadData);
                    }
                    return taskStateResponse;
                }
            }
        }


        private void CompleteOrder()
        {
            EndReocrdTrajectory();
            UnRegistPointsRegisted();
            ChangeTaskStatus(OrderTaskName, TASK_RUN_STATUS.ACTION_FINISH);
            taskCancel.Cancel();
            AgvSimulation.CancelTask();
        }

        internal void AbortOrder(TASK_DOWNLOAD_RETURN_CODES agv_task_return_code, ALARMS alarm_code = ALARMS.NONE, string message = "")
        {
            LOG.Critical(agv_task_return_code.ToString());
            UnRegistPointsRegisted();
            taskCancel.Cancel();
            AgvSimulation.CancelTask();

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
            var IsAllPointsUnRegisted = StaMap.UnRegistPointByName(AGV.Name, new int[] { AGV.states.Last_Visited_Node }, out int[] failTags);
            //Map.Points.Values.Where(pt => pt.RegistInfo != null).Where(pt => pt.RegistInfo.RegisterAGVName == AGV.Name);
            if (IsAllPointsUnRegisted)
            {
                LOG.WARN($"{AGV.Name}-交通解除註冊點完成");
            }
            else
            {
                LOG.WARN($"{AGV.Name}-交通解除註冊點{string.Join("、", failTags)}失敗");
            }

        }
        internal async void ChangeTaskStatus(string TaskName, TASK_RUN_STATUS status, string failure_reason = "")
        {
            if (TaskOrder == null)
                return;
            TaskOrder.State = status;
            if (status == TASK_RUN_STATUS.FAILURE | status == TASK_RUN_STATUS.CANCEL | status == TASK_RUN_STATUS.ACTION_FINISH | status == TASK_RUN_STATUS.WAIT)
            {
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

                TaskOrder = null;
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

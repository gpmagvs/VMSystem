﻿using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.VMS;
using AGVSystemCommonNet6.HttpTools;
using VMSystem.BackgroundServices;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.Controllers
{
    public class WebsocketClientMiddleware : WebsocketServerMiddleware
    {

        public static WebsocketClientMiddleware middleware = new WebsocketClientMiddleware(130);

        public WebsocketClientMiddleware(int publish_duraction) : base(publish_duraction)
        {

        }
        public override List<string> channelMaps { get; set; } = new List<string>()
        {
             "/ws",
             "/ws/DynamicTrafficData",
             "/ws/AGVNaviPathsInfo",
             "/ws/VMSAliveCheck",
             "/ws/VMSStatus",

        };
        protected override async Task CollectViewModelData()
        {
            try
            {
                CurrentViewModelDataOfAllChannel[channelMaps[0]] = new
                {
                    //DynamicTrafficData = ViewModelFactory.GetDynamicTrafficDataVM(),
                    //VMSAliveCheckVM = ViewModelFactory.GetVMSAliveCheckVM(),
                    AGVNaviPathsInfoVM = ViewModelFactory.GetAGVNaviPathsInfoVM(),
                    OtherAGVLocations = VMSManager.OthersAGVInfos.Values.ToList(),
                    VMSStatus = VehicleStateService.AGVStatueDtoStored.Values.OrderBy(d => d.AGV_Name).ToList(),
                };
            }
            catch (Exception ex)
            {
            }
        }
        private static class ViewModelFactory
        {
            public static object GetDynamicTrafficDataVM()
            {
                return TrafficControlCenter.DynamicTrafficState;
            }
            public static object GetAGVNaviPathsInfoVM()
            {
                object GetNavigationData(AGV.IAGV agv)
                {
                    if (agv.currentMapPoint == null)
                        return new { };
                    var taskRuningStatus = agv.taskDispatchModule.TaskStatusTracker.TaskRunningStatus;
                    var OrderHandler = agv.taskDispatchModule.OrderHandler;
                    List<int> navingTagList = GetNavigationTag(agv);
                    bool isOrderExecuting = agv.taskDispatchModule.OrderExecuteState == AGV.clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
                    return new
                    {
                        currentLocation = agv.currentMapPoint.TagNumber,
                        currentCoordication = agv.states.Coordination,
                        vehicleWidth = agv.options.VehicleWidth,
                        vehicleLength = agv.options.VehicleLength,
                        cargo_status = new
                        {
                            exist = agv.states.Cargo_Status == 1,
                            cargo_type = agv.states.CargoType,
                            cst_id = agv.states.CSTID.FirstOrDefault()
                        },
                        nav_path = isOrderExecuting ? navingTagList : OrderHandler.RunningTask.FuturePlanNavigationTags,
                        theta = agv.states.Coordination.Theta,
                        waiting_info = agv.taskDispatchModule.OrderHandler.RunningTask.TrafficWaitingState,
                        states = new
                        {
                            is_online = agv.online_state == ONLINE_STATE.ONLINE,
                            is_executing_task = taskRuningStatus == TASK_RUN_STATUS.NAVIGATING || taskRuningStatus == TASK_RUN_STATUS.ACTION_START,
                            main_status = agv.main_state
                        },
                        currentAction = agv.taskDispatchModule.TaskStatusTracker.currentActionType
                    };

                    List<int> GetNavigationTag(AGV.IAGV agv)
                    {
                        List<int> tags = agv.NavigationState.NextNavigtionPoints.GetTagCollection().DistinctBy(tag => tag).ToList();
                        return tags;
                    }
                }
                return VMSManager.AllAGV.ToDictionary(agv => agv.Name, agv => GetNavigationData(agv));
            }
            public static object GetVMSAliveCheckVM()
            {
                return true;
            }
            public static object GetVMSStatusData()
            {
                return VMSManager.GetVMSViewData();
            }
        }

    }
}

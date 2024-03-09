using AGVSystemCommonNet6.AGVDispatch.Messages;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;
using VMSystem.VMS;
using AGVSystemCommonNet6.HttpTools;

namespace VMSystem.Controllers
{
    public class WebsocketClientMiddleware:WebsocketServerMiddleware
    {

        public static WebsocketClientMiddleware middleware = new WebsocketClientMiddleware();
        public override List<string> channelMaps { get; set; }=new List<string>()
        {
             "/ws/DynamicTrafficData",
             "/ws/AGVNaviPathsInfo",
             "/ws/VMSAliveCheck",
             "/ws/VMSStatus",

        };
        protected override async Task CollectViewModelData()
        {
            try
            {

                CurrentViewModelDataOfAllChannel[channelMaps[0]] = ViewModelFactory.GetDynamicTrafficDataVM();
                CurrentViewModelDataOfAllChannel[channelMaps[1]] = ViewModelFactory.GetAGVNaviPathsInfoVM();
                CurrentViewModelDataOfAllChannel[channelMaps[2]] = ViewModelFactory.GetVMSAliveCheckVM();
                CurrentViewModelDataOfAllChannel[channelMaps[3]] = ViewModelFactory.GetVMSStatusData();
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
                    return new
                    {
                        currentLocation = agv.currentMapPoint.TagNumber,
                        currentCoordication = agv.states.Coordination,
                        cargo_status = new
                        {
                            exist = agv.states.Cargo_Status == 1,
                            cargo_type = agv.states.CargoType,
                            cst_id = agv.states.CSTID.FirstOrDefault()
                        },
                        nav_path = agv.main_state != MAIN_STATUS.RUN ? new List<int>() : agv.taskDispatchModule.OrderHandler.GetNavPathTags(),
                        theta = agv.states.Coordination.Theta,
                        waiting_info = agv.taskDispatchModule.OrderHandler.RunningTask.TrafficWaitingState,
                        states = new
                        {
                            is_online = agv.online_state == ONLINE_STATE.ONLINE,
                            is_executing_task = taskRuningStatus == TASK_RUN_STATUS.NAVIGATING | taskRuningStatus == TASK_RUN_STATUS.ACTION_START,
                            main_status = agv.main_state
                        },
                        currentAction = agv.taskDispatchModule.TaskStatusTracker.currentActionType
                    };
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

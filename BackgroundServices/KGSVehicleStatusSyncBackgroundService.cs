using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.MAP;
using KGSWebAGVSystemAPI;
using KGSWebAGVSystemAPI.Models;
using Microsoft.EntityFrameworkCore;
using RosSharp.RosBridgeClient;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.VMS;
using Task = System.Threading.Tasks.Task;
namespace VMSystem.BackgroundServices
{
    public class KGSVehicleStatusSyncBackgroundService : BackgroundService
    {
        IServiceScopeFactory serviceScopeFactory;
        WebAGVSystemContext dbcontext;
        public KGSVehicleStatusSyncBackgroundService(IServiceScopeFactory serviceScopeFactory)
        {
            this.serviceScopeFactory = serviceScopeFactory;

        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var scf = serviceScopeFactory.CreateScope();
            dbcontext = scf.ServiceProvider.GetRequiredService<WebAGVSystemContext>();

            _ = Task.Run(async () =>
            {

                while (true)
                {
                    var agvInfos = dbcontext.Agvinfos.AsNoTracking().ToList();

                    foreach (var agvInfo in agvInfos)
                    {
                        //Update IAGV State like report from agv.
                        ReportVehicleState(agvInfo);
                    }
                    await Task.Delay(500);
                }
            });

        }

        private async Task ReportVehicleState(Agvinfo agvInfo)
        {
            string agvName = "AGV_" + agvInfo.Agvid.ToString("X3");
            //search from gpm
            if (VMSManager.TryGetAGV(agvName, out IAGV gpmAGVInstance))
            {
                gpmAGVInstance.AgvID = agvInfo.Agvid;
                gpmAGVInstance.states = CreateStateData(agvInfo);
                gpmAGVInstance.online_state = (AGVSystemCommonNet6.clsEnums.ONLINE_STATE)agvInfo.Agvmode;
                gpmAGVInstance.connected = agvInfo.AgvconnectStatus == 1;
                gpmAGVInstance.taskDispatchModule.OrderHandler.OrderData.TaskName = agvInfo.DoTaskName; //把任務ID灌入
                //決定是否有在執行任務
                gpmAGVInstance.taskDispatchModule.OrderExecuteState = DetermineOrderExecuteState(agvInfo, gpmAGVInstance);
            }

        }

        private clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS DetermineOrderExecuteState(Agvinfo agvInfo, IAGV gpmAGVInstance)
        {
            if (gpmAGVInstance.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN)
                return clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.AGV_STATUS_ERROR;
            else if (gpmAGVInstance.online_state == AGVSystemCommonNet6.clsEnums.ONLINE_STATE.OFFLINE)
                return clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.AGV_OFFLINE;
            else
            {
                if (agvInfo.DoTaskName != null && agvInfo.DoTaskName != "")
                {

                    var runningTask = dbcontext.ExecutingTasks.AsNoTracking().FirstOrDefault(order => order.Name == agvInfo.DoTaskName);
                    if (runningTask != null)
                    {
                        var orderData = new AGVSystemCommonNet6.AGVDispatch.clsTaskDto
                        {
                            Action = (ACTION_TYPE)int.Parse(runningTask.ActionType),
                            DesignatedAGVName = gpmAGVInstance.Name,
                            DispatcherName = runningTask.AssignUserName,
                            Carrier_ID = runningTask.Cstid,
                            CST_TYPE = (int)runningTask.Csttype,
                            From_Station = StaMap.GetPointByIndex((int)runningTask.FromStationId).TagNumber + "",
                            To_Station = StaMap.GetPointByIndex((int)runningTask.ToStationId).TagNumber + "",
                            TaskName = runningTask.Name
                        };
                        gpmAGVInstance.taskDispatchModule.OrderHandler.OrderData = orderData;
                        gpmAGVInstance.taskDispatchModule.OrderHandler.RunningTask = new MoveToDestineTask(gpmAGVInstance, orderData);
                        return clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING;
                    }
                    else
                    {
                        return clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.NO_ORDER;
                    }
                }
                else
                    return clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.NO_ORDER;
            }


        }

        private clsRunningStatus CreateStateData(Agvinfo agvInfo)
        {
            clsRunningStatus status = new clsRunningStatus();
            status.AGV_Status = (AGVSystemCommonNet6.clsEnums.MAIN_STATUS)agvInfo.AgvmainStatus;
            try
            {
                MapPoint currentMapPoint = StaMap.Map.Points[(int)agvInfo.CurrentPos];
                status.Last_Visited_Node = currentMapPoint.TagNumber;
                status.Coordination.X = currentMapPoint.X;
                status.Coordination.Y = currentMapPoint.Y;
            }
            catch (Exception ex)
            {
            }
            status.Electric_Volume = new double[2] { (double)agvInfo.Battery, (double)agvInfo.Battery2 };
            status.CSTID = new string[1] { agvInfo.Cstid };
            return status;
        }
    }
}

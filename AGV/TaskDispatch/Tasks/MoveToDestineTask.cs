﻿using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using AGVSystemCommonNet6.Notify;
using System.Data;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToDestineTask : MoveTaskDynamicPathPlanV2
    {
        public MoveToDestineTask() : base()
        { }

        public MoveToDestineTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Destine;
        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                if (OrderData.Action == ACTION_TYPE.Carry)
                {
                    var destinePt = StaMap.GetPointByTagNumber(OrderData.To_Station_Tag);
                    NotifyServiceHelper.INFO($"{Agv.Name} Start Go To Destine({destinePt.Graph.Display}) Of Carry Order");
                    bool isSourceAGV = OrderData.IsFromAGV;
                    clsAGVSTaskReportResponse responseOfStartTrasferRpt = await AGVSSerivces.TRANSFER_TASK.StartTransferCargoReport(this.Agv.Name, OrderData.From_Station_Tag, OrderData.To_Station_Tag, OrderData.From_Slot, OrderData.To_Slot, isSourceAGV);
                    if (!responseOfStartTrasferRpt.confirm)
                    {
                        return (false, responseOfStartTrasferRpt.AlarmCode, responseOfStartTrasferRpt.message);
                    }
                }
                if (this.Agv.IsAGVHasCargoOrHasCargoID() == true)
                    OrderData.Actual_Carrier_ID = this.Agv.states.CSTID[0];
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode, response.message);
            }
            return await base.DistpatchToAGV();
        }
    }
}

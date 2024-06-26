﻿using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Microservices.AGVS;
using AGVSystemCommonNet6.Microservices.ResponseModel;
using System.Data;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class MoveToDestineTask : NavigateToGoalTask
    {
        public MoveToDestineTask() : base()
        { }

        public MoveToDestineTask(IAGV Agv, clsTaskDto order) : base(Agv, order)
        {
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.Traveling_To_Destine;
        internal override async Task<(bool confirmed, ALARMS alarm_code)> DistpatchToAGV()
        {
            if (!OrderData.bypass_eq_status_check)
            {
                //clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData.need_change_agv ? OrderData.TransferToTag : OrderData.To_Station_Tag, this, OrderData.Action);
                clsAGVSTaskReportResponse response = await VMSystem.Services.AGVSServicesTool.LoadUnloadActionStartReport(OrderData, this);
                if (response.confirm == false)
                    return (response.confirm, response.AlarmCode);
            }
            return await base.DistpatchToAGV();
        }
    }
}

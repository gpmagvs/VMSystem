using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class VehicleCargoRemoveRequestTask : TaskBase
    {
        public VehicleCargoRemoveRequestTask()
        {
        }
        public VehicleCargoRemoveRequestTask(IAGV Agv, clsTaskDto orderData) : base(Agv, orderData)
        {
        }

        public VehicleCargoRemoveRequestTask(IAGV Agv, clsTaskDto orderData, SemaphoreSlim taskTbModifyLock) : base(Agv, orderData, taskTbModifyLock)
        {
        }
        internal override async Task<(bool confirmed, ALARMS alarm_code, string message)> DistpatchToAGV()
        {
            await RemoveCarrierRequest();
            return (true, ALARMS.NONE, "");
        }
        private async Task RemoveCarrierRequest()
        {
            if (Agv.options.Simulation)
            {
                Agv.AgvSimulation.RemoveCargo();
                return;
            }
            //http://10.22.141.215:7025/api/VMS/RemoveCassette
            var response = await Agv.AGVHttp.PostAsync($"/api/VMS/RemoveCassette", null);
            logger.Debug(response);
        }

        public override VehicleMovementStage Stage { get; set; } = VehicleMovementStage.WorkingAtDestination;

        public override ACTION_TYPE ActionType => ACTION_TYPE.Load;
        public override void DetermineThetaOfDestine(clsTaskDownloadData _taskDownloadData)
        {
        }

        public override void HandleTrafficControlAction(clsMoveTaskEvent confirmArg, ref clsTaskDownloadData OriginalTaskDownloadData)
        {
        }
    }
}

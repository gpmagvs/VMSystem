using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.DATABASE;
using System.Diagnostics;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;

namespace VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers
{
    public class ChargeStationChanger : DestineChangeBase
    {

        public ChargeStationChanger(IAGV agv, clsTaskDto order, SemaphoreSlim taskTableLocker) : base(agv, order, taskTableLocker)
        {
        }

        /// <summary>
        /// 實作是否需要更換充電站
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        internal override bool IsNeedChange()
        {
            if (IsAnyVehicleStatusDownAtCharger() || IsAnyVehicleStatusDownAtChargerEntryPoint())
                return true;
            if (IsAnyVehicleNextWorkStationIsDestine())
                return true;
            if (IsWaitTrafficControlTimeTooLong())
                return true;
            return false;
        }
        protected override int GetNewDestineTag()
        {
            return -1;//回傳-1讓系統再自動搜尋充電站
        }
        /// <summary>
        /// 是否有任何車輛在目標充電站內且狀態是Down
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        private bool IsAnyVehicleStatusDownAtCharger()
        {
            return othersVehicles.Any(v => v.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN &&
                                           v.currentMapPoint.TagNumber == destineTag);
        }

        private bool IsAnyVehicleStatusDownAtChargerEntryPoint()
        {
            return othersVehicles.Any(v => v.main_state == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN &&
                                           destineMapPoint.TargetNormalPoints().Any(pt => pt.TagNumber == v.currentMapPoint.TagNumber));
        }

        private bool IsAnyVehicleNextWorkStationIsDestine()
        {

            return othersVehicles.Where(v => v.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING)
                                 .Where(v => v.GetNextWorkStationTag() == destineTag)
                                 .Any();
        }

        private bool IsWaitTrafficControlTimeTooLong()
        {
            var agvNavigationState = agv.NavigationState;
            if (!agvNavigationState.IsWaitingConflicSolve)
                return false;
            return (DateTime.Now - agvNavigationState.StartWaitConflicSolveTime).TotalSeconds > (Debugger.IsAttached ? 10 : 30);
        }
    }
}

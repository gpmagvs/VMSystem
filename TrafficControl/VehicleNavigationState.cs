using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using SQLitePCL;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch;

namespace VMSystem.TrafficControl
{
    public class VehicleNavigationState
    {

        public enum REGION_CONTROL_STATE
        {
            WAIT_AGV_CYCLE_STOP,
            WAIT_AGV_REACH_ENTRY_POINT,
            NONE
        }

        public enum NAV_STATE
        {
            WAIT_SOLVING,
            RUNNING,
            IDLE,
            WAIT_REGION_ENTERABLE,
            WAIT_TAG_PASSABLE_BY_EQ_PARTS_REPLACING
        }

        public static Map CurrentMap => StaMap.Map;
        public NAV_STATE State { get; set; } = NAV_STATE.IDLE;
        public REGION_CONTROL_STATE RegionControlState { get; set; } = REGION_CONTROL_STATE.NONE;
        public IAGV Vehicle { get; set; }
        public MapPoint CurrentMapPoint
        {
            get => _CurrentMapPoint;
            set
            {
                if (_CurrentMapPoint == value)
                    return;
                _CurrentMapPoint = value;
                CurrentRegion = CurrentMapPoint.GetRegion(CurrentMap);
                var currentPtInNavitaion = NextNavigtionPoints.FirstOrDefault(pt => pt.TagNumber == value.TagNumber);
                if (currentPtInNavitaion != null)
                {
                    var _index = NextNavigtionPoints.ToList().FindIndex(pt => pt == currentPtInNavitaion);
                    UpdateNavigationPoints(NextNavigtionPoints.Skip(_index).ToList());
                }
                else
                {
                    ResetNavigationPoints();
                }
            }
        }
        private MapPoint _CurrentMapPoint = new MapPoint();

        private MapRegion _CurrentRegion = new MapRegion();
        public MapRegion CurrentRegion
        {
            get => _CurrentRegion;
            set
            {
                if (_CurrentRegion == value) return;
                _CurrentRegion = value;
                Log($"Region Change to {value.Name}(Is Narrow Path?{value.IsNarrowPath})");
            }
        }
        public IEnumerable<MapPoint> NextNavigtionPoints { get; private set; } = new List<MapPoint>();
        /// <summary>
        /// 當前與剩餘路徑佔據的道路
        /// </summary>
        public List<MapPath> OcuupyPathes
        {
            get
            {
                List<MapPath> output = new List<MapPath>();
                var map = CurrentMap;
                var nextNavigtionPointOcuupyPathes = NextNavigtionPoints.SelectMany(point => point.GetPathes(ref map));
                output.AddRange(nextNavigtionPointOcuupyPathes);
                output.AddRange(CurrentMapPoint.GetPathes(ref map));
                output = output.Distinct().ToList();
                return output;
            }
        }

        public List<MapRectangle> OccupyRegions
        {
            get
            {
                var _nexNavPts = this.NextNavigtionPoints.ToList();
                if (!_nexNavPts.Any())
                    return new List<MapRectangle>()
                    {
                         Vehicle.AGVGeometery
                    };



                bool containNarrowPath = _nexNavPts.Any(pt => pt.GetRegion(CurrentMap).IsNarrowPath);
                var vWidth = Vehicle.options.VehicleWidth / 100.0 + (containNarrowPath ? 0.0 : 0);
                var vLength = Vehicle.options.VehicleLength / 100.0 + (containNarrowPath ? 0.0 : 0); ;
                List<MapRectangle> output = new List<MapRectangle>() { Vehicle.AGVGeometery };
                output.AddRange(Tools.GetPathRegionsWithRectangle(_nexNavPts, vWidth, vLength));
                output.AddRange(Tools.GetPathRegionsWithRectangle(new List<MapPoint> { output.Last().EndPointTag }, vLength, vLength));
                return output;
            }
        }

        public ConflicSolveResult.CONFLIC_ACTION ConflicAction { get; internal set; } = ConflicSolveResult.CONFLIC_ACTION.ACCEPT_GO;

        public void UpdateNavigationPoints(IEnumerable<MapPoint> pathPoints)
        {
            NextNavigtionPoints = pathPoints.ToList();
        }

        public void ResetNavigationPoints()
        {

            try
            {
                //var currentTask = Vehicle.CurrentRunningTask();
                //if (currentTask.ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None)
                //    (currentTask as MoveTaskDynamicPathPlanV2).UpdateMoveStateMessage($"Reset Nav Pts at {this.Vehicle.currentMapPoint.TagNumber}");

            }
            catch (Exception ex)
            {
            }
            UpdateNavigationPoints(new List<MapPoint> { _CurrentMapPoint });
        }

        private void Log(string message)
        {
            LOG.INFO($"[VehicleNavigationState]-[{Vehicle.Name}] " + message);
        }

        internal void StateReset()
        {
            State = VehicleNavigationState.NAV_STATE.IDLE;
            RegionControlState = REGION_CONTROL_STATE.NONE;
        }
    }
}

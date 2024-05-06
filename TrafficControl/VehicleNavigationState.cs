using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;

namespace VMSystem.TrafficControl
{
    public class VehicleNavigationState
    {
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
        public IAGV Vehicle { get; set; }
        public MapPoint CurrentMapPoint
        {
            get => _CurrentMapPoint;
            set
            {
                if (_CurrentMapPoint == value) return;
                _CurrentMapPoint = value;
                CurrentRegion = CurrentMapPoint.GetRegion(CurrentMap);
                var currentPtInNavitaion = NextNavigtionPoints.FirstOrDefault(pt => pt == value);
                if (currentPtInNavitaion != null)
                {
                    var _index = NextNavigtionPoints.ToList().FindIndex(pt => pt == currentPtInNavitaion);
                    NextNavigtionPoints = NextNavigtionPoints.Skip(_index);
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
                var _nexNavPts= this.NextNavigtionPoints.ToList();
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
                return output;
            }
        }

        public void UpdateNavigationPoints(IEnumerable<MapPoint> pathPoints)
        {
            NextNavigtionPoints = pathPoints.ToList();
        }

        public void ResetNavigationPoints()
        {
            UpdateNavigationPoints(new List<MapPoint> { this.Vehicle.currentMapPoint });
        }

        private void Log(string message)
        {
            LOG.INFO($"[VehicleNavigationState]-[{Vehicle.Name}] " + message);
        }
    }
}

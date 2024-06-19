using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using System.Drawing;
using VMSystem.AGV;

namespace VMSystem.TrafficControl.ConflicDetection
{
    /// <summary>
    /// 進出工作站衝突檢測 (Enter Work Station Detection)
    /// </summary>
    public class EnterWorkStationDetection : ConflicDetectionBase
    {
        public EnterWorkStationDetection(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect) : base(DetectPoint, ThetaOfPridiction, AGVToDetect)
        {
        }

        protected override IEnumerable<IAGV> GetOtherVehicles()
        {
            return base.GetOtherVehicles().Where(agv=>agv.currentMapPoint.StationType== MapPoint.STATION_TYPE.Normal)
                                          .Where(agv => agv.CurrentRunningTask().ActionType == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.None);
        }
        public override clsConflicDetectResultWrapper Detect()
        {
            //偵測是否有其他車輛會通過二次定位=>工作站之間即可
            PointF start = new PointF((float)this.AGVToDetect.states.Coordination.X, (float)this.AGVToDetect.states.Coordination.Y);
            PointF end = new PointF((float)this.DetectPoint.X, (float)this.DetectPoint.Y);

            MapRectangle pathRectangle = Tools.CreatePathRectangle(start, end, (float)(AGVToDetect.options.VehicleWidth / 100.0), (float)(AGVToDetect.options.VehicleLength / 100.0));

            List<IAGV> conflicVehicles = OtherAGV.Where(agv => agv.NavigationState.NextPathOccupyRegions.Any(rect => rect.IsIntersectionTo(pathRectangle))).ToList();
            if (conflicVehicles.Any())
            {
                string agvNames = conflicVehicles.GetNames();
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, $"等待{agvNames}通過..");
            }
            else
            {
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.OK, "");
            }
        }
    }
}

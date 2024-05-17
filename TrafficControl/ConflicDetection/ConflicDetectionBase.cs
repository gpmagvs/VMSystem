using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.VMS;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public abstract class ConflicDetectionBase
    {
        public readonly MapPoint DetectPoint;
        public readonly IAGV AGVToDetect;
        public readonly double ThetaOfPridiction;
        /// <summary>
        /// 計算干涉時車輛[長度]膨脹係數
        /// </summary>
        public double AGVLengthExpandRatio = 1.0;
        /// <summary>
        /// 計算干涉時車輛[寬度]膨脹係數
        /// </summary>
        public double AGVWidthExpandRatio = 1.0;
        public IEnumerable<IAGV> OtherAGV => VMSManager.AllAGV.FilterOutAGVFromCollection(AGVToDetect);

        public ConflicDetectionBase(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect)
        {
            this.DetectPoint = DetectPoint;
            this.AGVToDetect = AGVToDetect;
            this.ThetaOfPridiction = ThetaOfPridiction;
        }

        public virtual clsConflicDetectResultWrapper Detect()
        {
            clsConflicDetectResultWrapper _baseResult = new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, "");
            if (IsRegisted(out IAGV registedAGV))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.Message = $"Point({DetectPoint.GetName()}) be registed by {registedAGV.Name}";
            }
            else if (IsConflicToOtherVehicleBody(out List<IAGV> conflicAGVList))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.Message = $"Point({DetectPoint.GetName()}) is conflic to {conflicAGVList.GetNames()}";
            }
            else if (IsConflicToOtherVehicleNavigatingPath(out Dictionary<IAGV, List<MapRectangle>> conflicState))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.Message = $"Point({DetectPoint.GetName()}) is conflic to navigating path of {conflicState.Keys.GetNames()} ";
            }
            else
            {
                _baseResult.Result = DETECTION_RESULT.OK;
            }
            return _baseResult;
        }

        public bool IsConflicToOtherVehicleBody(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();
            MapRectangle RectangleOfDetectPoint = GetRectangleOfDetectPoint();

            conflicAGVList = OtherAGV.Where(_agv => _agv.AGVRealTimeGeometery.IsIntersectionTo(RectangleOfDetectPoint))
                                     .ToList();

            return conflicAGVList.Any();
        }
        /// <summary>
        /// 是否被註冊
        /// </summary>
        /// <param name="RegistedAGV"></param>
        /// <returns></returns>
        public bool IsRegisted(out IAGV RegistedAGV)
        {
            RegistedAGV = null;
            KeyValuePair<int, clsPointRegistInfo> regist = StaMap.RegistDictionary.Where(keyPair => keyPair.Value.RegisterAGVName != AGVToDetect.Name)
                                    .FirstOrDefault(keyPair => keyPair.Key == DetectPoint.TagNumber);

            if (regist.Value == null)
                return false;

            RegistedAGV = VMSManager.GetAGVByName(regist.Value.RegisterAGVName);
            return true;
        }

        /// <summary>
        /// 評估點是否會與其他車輛當前行駛路徑干涉
        /// </summary>
        /// <param name="conflicState"></param>
        /// <returns></returns>
        public bool IsConflicToOtherVehicleNavigatingPath(out Dictionary<IAGV, List<MapRectangle>> conflicState)
        {
            MapRectangle RectangleOfDetectPoint = GetRectangleOfDetectPoint();
            Dictionary<IAGV, IEnumerable<MapRectangle>> pathConflicStates = OtherAGV.ToDictionary(agv => agv, agv => agv.NavigationState.NextPathOccupyRegions.Where(rect => rect.IsIntersectionTo(RectangleOfDetectPoint)));
            conflicState = pathConflicStates.Where(kp => kp.Value.Any()).ToDictionary(kp => kp.Key, kp => kp.Value.ToList());
            return conflicState.Any();
        }



        private MapRectangle GetRectangleOfDetectPoint()
        {
            double x = DetectPoint.X;
            double y = DetectPoint.Y;
            double theta = ThetaOfPridiction;
            double width = AGVToDetect.options.VehicleWidth / 100.0 * AGVWidthExpandRatio;
            double length = AGVToDetect.options.VehicleLength / 100.0 * AGVLengthExpandRatio;
            return Tools.CreateRectangle(x, y, theta, width, length);
        }

    }
    public enum DETECTION_RESULT
    {
        OK, NG
    }

    public class clsConflicDetectResultWrapper
    {
        public DETECTION_RESULT Result { get; set; } = DETECTION_RESULT.NG;
        public string Message { get; set; } = "";

        public clsConflicDetectResultWrapper(DETECTION_RESULT Result, string Message)
        {
            this.Result = Result;
            this.Message = Message;
        }
    }


    public static class ExtensionsOfConflicDetection
    {
        /// <summary>
        /// 取出車輛名稱集合字串
        /// </summary>
        /// <param name="agvList"></param>
        /// <returns></returns>
        public static string GetNames(this IEnumerable<IAGV> agvList)
        {
            return string.Join(",", agvList.Where(agv => agv != null).Select(agv => agv.Name));
        }

        /// <summary>
        /// 取得站點名稱
        /// </summary>
        /// <param name="mapPoint"></param>
        /// <returns></returns>
        public static string? GetName(this MapPoint? mapPoint)
        {
            return mapPoint?.Graph.Display;
        }
    }
}

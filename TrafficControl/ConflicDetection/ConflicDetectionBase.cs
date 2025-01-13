using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.VMS;
using static VMSystem.TrafficControl.clsTrafficControlParameters;
using NLog;
using VMSystem.Extensions;

namespace VMSystem.TrafficControl.ConflicDetection
{
    public abstract class ConflicDetectionBase
    {
        public readonly MapPoint DetectPoint;
        public readonly IAGV AGVToDetect;
        public readonly double ThetaOfPridiction;
        protected Logger logger = LogManager.GetLogger("ConflicDetection");
        public virtual clsVehicleGeometryExpand GeometryExpand { get; set; } = new clsVehicleGeometryExpand();

        public IEnumerable<IAGV> OtherAGV => GetOtherVehicles();

        protected virtual IEnumerable<IAGV> GetOtherVehicles()
        {
            return VMSManager.AllAGV.FilterOutAGVFromCollection(AGVToDetect);
        }

        public ConflicDetectionBase(MapPoint DetectPoint, double ThetaOfPridiction, IAGV AGVToDetect)
        {
            this.DetectPoint = DetectPoint;
            this.AGVToDetect = AGVToDetect;
            this.ThetaOfPridiction = ThetaOfPridiction;
        }

        public virtual clsConflicDetectResultWrapper Detect()
        {
            logger.Info($"{this.GetType().Name} Basic Detect start: DetectPoint(Tag)={DetectPoint.TagNumber}/AGV={AGVToDetect.Name}/ThetaOfPridiction={ThetaOfPridiction}.");
            clsConflicDetectResultWrapper _baseResult = new clsConflicDetectResultWrapper(DETECTION_RESULT.NG, "");

            if (IsRegisted(out IAGV registedAGV))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.ConflicStatusCode = CONFLIC_STATUS_CODE.REGISTED;
                _baseResult.ConflicToAGVList = new() { registedAGV };
                _baseResult.Message = $"Point({DetectPoint.GetName()}) be registed by {registedAGV.Name}";
                return _baseResult;
            }
            if (OtherAGV.Count() == 0)
            {
                return new clsConflicDetectResultWrapper(DETECTION_RESULT.OK, "");
            }
            logger.Info($"OtherAGV={OtherAGV.GetNames()}");

            if (IsConflicToOtherVehicleBody(out List<IAGV> conflicAGVList))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.ConflicStatusCode = CONFLIC_STATUS_CODE.CONFLIC_TO_OTHER_AGV_BODY;
                _baseResult.ConflicToAGVList = conflicAGVList;
                _baseResult.Message = $"Point({DetectPoint.GetName()}) is conflic to {conflicAGVList.GetNames()}\r\n([{this.GetType().Name}] Body Detection)";
            }
            else if (IsConflicToOtherVehicleRotaionBody(out conflicAGVList))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.ConflicStatusCode = CONFLIC_STATUS_CODE.CONFLIC_TO_OTHER_AGV_BODY;
                _baseResult.ConflicToAGVList = conflicAGVList;
                _baseResult.Message = $"Point({DetectPoint.GetName()}) is conflic to {conflicAGVList.GetNames()}\r\n([{this.GetType().Name}] Rotation Detection)";
            }
            else if (IsConflicToOtherVehicleNavigatingPath(out Dictionary<IAGV, List<MapRectangle>> conflicState))
            {
                _baseResult.Result = DETECTION_RESULT.NG;
                _baseResult.ConflicStatusCode = CONFLIC_STATUS_CODE.CONFLIC_TO_OTHER_NAVIGATING_PATH;
                _baseResult.ConflicToAGVList = conflicState.Keys.ToList();
                _baseResult.Message = $"Point({DetectPoint.GetName()}) is conflic to navigating path of {conflicState.Keys.GetNames()} ";
            }
            else
            {
                _baseResult.Result = DETECTION_RESULT.OK;
            }

            logger.Info($"Detect result: {(_baseResult.Result == DETECTION_RESULT.OK ? "OK" : "NG")},ConflicStatusCode: {_baseResult.ConflicStatusCode.ToString()}, Message: {(_baseResult.Result == DETECTION_RESULT.OK ? "" : _baseResult.Message)}");
            //log ConflicToAGVList
            if (_baseResult.ConflicToAGVList.Any())
            {
                logger.Info($"ConflicToAGVList: {_baseResult.ConflicToAGVList.GetNames()}");
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
        public virtual bool IsConflicToOtherVehicleRotaionBody(out List<IAGV> conflicAGVList)
        {
            conflicAGVList = new();
            MapRectangle RectangleOfDetectPoint = GetRotationRectangeOfDetectPoint();
            conflicAGVList = OtherAGV.Where(_agv => _IsConflicTo(RectangleOfDetectPoint, _agv))
                                     .ToList();

            bool _IsConflicTo(MapRectangle detectRectangle, IAGV detectToAgv)
            {
                if (!detectToAgv.IsVehicleAtWorkStation())
                    return detectToAgv.AGVRotaionGeometry.IsIntersectionTo(detectRectangle);
                else
                    return detectToAgv.AGVRealTimeGeometery.IsIntersectionTo(detectRectangle);
            }


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
            conflicState = pathConflicStates.Where(kp => kp.Value.Any())
                                            .ToDictionary(kp => kp.Key, kp => kp.Value.ToList());
            return conflicState.Any();
        }



        protected virtual MapRectangle GetRectangleOfDetectPoint()
        {
            double x = DetectPoint.X;
            double y = DetectPoint.Y;
            double theta = ThetaOfPridiction;
            double width = AGVToDetect.options.VehicleWidth / 100.0 * GeometryExpand.Width;
            double length = AGVToDetect.options.VehicleLength / 100.0 * GeometryExpand.Length;
            return Tools.CreateRectangle(x, y, theta, width, length);
        }
        protected MapRectangle GetRotationRectangeOfDetectPoint()
        {
            double x = DetectPoint.X;
            double y = DetectPoint.Y;
            double theta = ThetaOfPridiction;
            double length = AGVToDetect.options.VehicleLength / 100.0 * GeometryExpand.Length;
            return Tools.CreateRectangle(x, y, theta, length, length);
        }

        public bool IsOtherVehiclesCurrentLocationSoFar()
        {
            var _DistancesToCurrentPoint = OtherAGV.Select(_agv => DetectPoint.CalculateDistance(_agv.states.Coordination));
            return _DistancesToCurrentPoint.All(distance => distance >= 3);
        }

    }
    public enum DETECTION_RESULT
    {
        OK, NG
    }
    public enum CONFLIC_STATUS_CODE
    {
        UNKNOWN = 40,
        OK = 200,
        REGISTED = 300,
        CONFLIC_TO_OTHER_NAVIGATING_PATH = 301,
        CONFLIC_TO_OTHER_AGV_BODY = 302,
    }
    public class clsConflicDetectResultWrapper
    {
        public DETECTION_RESULT Result { get; set; } = DETECTION_RESULT.NG;
        public CONFLIC_STATUS_CODE ConflicStatusCode { get; set; } = CONFLIC_STATUS_CODE.OK;
        public string Message { get; set; } = "";

        public List<IAGV> ConflicToAGVList { get; set; } = new();


        public clsConflicDetectResultWrapper(DETECTION_RESULT Result, string Message)
        {
            this.Result = Result;
            this.Message = Message;
        }
    }



}

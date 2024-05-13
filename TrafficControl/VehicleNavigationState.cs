using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using Microsoft.VisualBasic;
using SQLitePCL;
using System.Drawing;
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
            WAIT_TAG_PASSABLE_BY_EQ_PARTS_REPLACING,
            AVOIDING_PATH
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
                    ResetNavigationPointsOfPathCalculation();
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
        public IEnumerable<MapPoint> NextNavigtionPointsForPathCalculation { get; private set; } = new List<MapPoint>();
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

        public List<MapRectangle> NextPathOccupyRegionsForPathCalculation
        {
            get
            {
                List<MapRectangle> output = _CreatePathOcuupyRegions(NextNavigtionPointsForPathCalculation.ToList());
                return output;
            }
        }

        public List<MapRectangle> NextPathOccupyRegions
        {
            get
            {
                List<MapRectangle> output = _CreatePathOcuupyRegions(NextNavigtionPoints.ToList());
                return output;
            }
        }

        private List<MapRectangle> _CreatePathOcuupyRegions(List<MapPoint> _nexNavPts)
        {
            var output = new List<MapRectangle>() { Vehicle.AGVRealTimeGeometery };

            if (!_nexNavPts.Any())
                return new List<MapRectangle>()
                    {
                         Vehicle.AGVRealTimeGeometery,
                         Vehicle.AGVCurrentPointGeometery
                    };

            bool containNarrowPath = _nexNavPts.Any(pt => pt.GetRegion(CurrentMap).IsNarrowPath);
            double _GeometryExpandRatio = IsCurrentPointIsLeavePointOfChargeStation() ? 1.0 : 1.2;

            var vWidth = Vehicle.options.VehicleWidth / 100.0 + (containNarrowPath ? 0.0 : 0);
            var vLength = Vehicle.options.VehicleLength / 100.0 + (containNarrowPath ? 0.0 : 0);

            var vLengthExpanded = vLength * _GeometryExpandRatio;


            MapPoint endPoint = _nexNavPts.Last();
            var pathForCalulate = _nexNavPts.Skip(1).ToList();
            if (pathForCalulate.Count > 1)
            {
                double lastAngle = _GetForwardAngle(pathForCalulate.First(), pathForCalulate.Count > 1 ? pathForCalulate[1] : pathForCalulate.First());
                bool _infrontOfChargeStation = Vehicle.currentMapPoint.TargetWorkSTationsPoints().ToList().Any(pt => pt.IsCharge);
                if (Math.Abs(Vehicle.states.Coordination.Theta - lastAngle) > 10)
                {
                    if (_infrontOfChargeStation)
                    {
                        output.Add(Vehicle.AGVRealTimeGeometery);
                    }
                    else
                        output.Add(Tools.CreateSquare(Vehicle.currentMapPoint, vLengthExpanded));
                }

                for (int i = 1; i < pathForCalulate.Count - 1; i++) //0 1 2 3 4 5 
                {
                    var _startPt = pathForCalulate[i];
                    var _endPt = pathForCalulate[i + 1];
                    double forwardAngle = _GetForwardAngle(_startPt, _endPt);

                    if (Math.Abs(forwardAngle - lastAngle) > 10)
                    {
                        output.Add(Tools.CreateSquare(_startPt, vLengthExpanded));
                    }
                    lastAngle = forwardAngle;
                }

            }


            output.AddRange(Tools.GetPathRegionsWithRectangle(_nexNavPts, vWidth, vLengthExpanded).Where(p => !double.IsNaN(p.Theta)).ToList());
            output.AddRange(Tools.GetPathRegionsWithRectangle(new List<MapPoint> { endPoint }, vLengthExpanded, vLengthExpanded));

            if (Vehicle.taskDispatchModule.OrderExecuteState == clsAGVTaskDisaptchModule.AGV_ORDERABLE_STATUS.EXECUTING && Vehicle.currentMapPoint.StationType == MapPoint.STATION_TYPE.Normal)
            {
                MapRectangle finalStopRectangle = IsCurrentGoToChargeAndNextStopPointInfrontOfChargeStation() || State == NAV_STATE.AVOIDING_PATH ?
                                                   Tools.CreateRectangle(endPoint.X, endPoint.Y, endPoint.Direction, vWidth, vLength) : Tools.CreateSquare(endPoint, vLengthExpanded);
                finalStopRectangle.StartPointTag = finalStopRectangle.EndMapPoint = endPoint;
                output.Add(finalStopRectangle);
            }
            double finalStopAngle = output.Last().Theta;

            //LOG.WARN($"停車點角度=>{output.Last().Theta}");
            return output;


            double _GetForwardAngle(MapPoint start, MapPoint end)
            {
                var startPtf = new PointF((float)start.X, (float)start.Y);
                var endPtf = new PointF((float)end.X, (float)end.Y);
                return Tools.CalculationForwardAngle(startPtf, endPtf);
            }

            bool IsCurrentPointIsLeavePointOfChargeStation()
            {
                var _previousTask = Vehicle.PreviousSegmentTask();
                if (_previousTask == null)
                {
                    return false;
                }
                if (!_previousTask.TaskDonwloadToAGV.ExecutingTrajecory.Any())
                    return false;
                var _previoseTaskStartPoint = StaMap.GetPointByTagNumber(_previousTask.TaskDonwloadToAGV.ExecutingTrajecory.First().Point_ID);
                bool isLeaveFromCharge = _previousTask.ActionType == ACTION_TYPE.Discharge && _previoseTaskStartPoint.IsCharge;
                if (!isLeaveFromCharge)
                    return false;
                return _previoseTaskStartPoint.TargetNormalPoints().GetTagCollection().Any(tag => tag == Vehicle.currentMapPoint.TagNumber);
            }

            bool IsCurrentGoToChargeAndNextStopPointInfrontOfChargeStation()
            {
                var _runningTask = Vehicle.CurrentRunningTask();
                if (_runningTask.OrderData==null|| _runningTask.OrderData.Action != ACTION_TYPE.Charge || _runningTask.ActionType != ACTION_TYPE.None)
                    return false;

                MapPoint ChargeStationPoint = StaMap.GetPointByTagNumber(_runningTask.OrderData.To_Station_Tag);
                var tagsOfStationEntry = ChargeStationPoint.TargetNormalPoints().Select(pt => pt.TagNumber).ToList();
                return tagsOfStationEntry.Any() && tagsOfStationEntry.Contains(_nexNavPts.Last().TagNumber);
            }
        }

        private double _FinalTheta = 0;
        public double FinalTheta
        {
            get => _FinalTheta;
            set
            {
                if (_FinalTheta != value)
                {
                    _FinalTheta = value;
                    LOG.WARN($"停車角度更新: {value}");
                }
            }
        }

        private bool _IsWaitingConflicSolve = false;

        public DateTime StartWaitConflicSolveTime = DateTime.MinValue;

        public bool IsWaitingConflicSolve
        {
            get => _IsWaitingConflicSolve;
            set
            {
                if (_IsWaitingConflicSolve != value)
                {
                    _IsWaitingConflicSolve = value;
                    if (_IsWaitingConflicSolve)
                        StartWaitConflicSolveTime = DateTime.Now;
                    else
                        StartWaitConflicSolveTime = DateTime.MinValue;
                }
            }
        }
        public bool IsConflicSolving { get; set; } = false;
        public MapPoint AvoidPt { get; internal set; }
        public bool IsAvoidRaising { get; internal set; } = false;
        public IAGV AvoidToVehicle { get; internal set; }

        public void UpdateNavigationPointsForPathCalculation(IEnumerable<MapPoint> pathPoints)
        {
            var _pathPoints = pathPoints.Clone().ToList();
            var runningTask = Vehicle.CurrentRunningTask();
            var orderData = runningTask.OrderData;
            _pathPoints.Last().Direction = _pathPoints.GetStopDirectionAngle(orderData, Vehicle, runningTask.Stage, _pathPoints.Last());
            List<MapPoint> output = new List<MapPoint>() { Vehicle.currentMapPoint };
            output.AddRange(_pathPoints);
            NextNavigtionPointsForPathCalculation = output.Where(pt => pt.TagNumber != 0).Distinct().ToList();
            //LOG.TRACE($"[{Vehicle.Name}] Update NavigationPointsForPathCalculation: {string.Join("->", NextNavigtionPointsForPathCalculation.Select(pt => pt.TagNumber))} ");
        }

        public void UpdateNavigationPoints(IEnumerable<MapPoint> pathPoints)
        {
            List<MapPoint> output = new List<MapPoint>() { Vehicle.currentMapPoint };
            output.AddRange(pathPoints.Clone());
            NextNavigtionPointsForPathCalculation = NextNavigtionPoints = output.Distinct().ToList();
        }

        public void ResetNavigationPoints()
        {
            var currentMpt = Vehicle.currentMapPoint;
            UpdateNavigationPoints(new List<MapPoint> { currentMpt });
        }
        public void ResetNavigationPointsOfPathCalculation()
        {
            var currentMpt = Vehicle.currentMapPoint;
            UpdateNavigationPointsForPathCalculation(new List<MapPoint> { currentMpt });
        }


        private void Log(string message)
        {
            LOG.INFO($"[VehicleNavigationState]-[{Vehicle.Name}] " + message);
        }

        internal void StateReset()
        {
            State = VehicleNavigationState.NAV_STATE.IDLE;
            RegionControlState = REGION_CONTROL_STATE.NONE;
            IsConflicSolving = IsWaitingConflicSolve = false;
            IsAvoidRaising = false;
        }
    }
}

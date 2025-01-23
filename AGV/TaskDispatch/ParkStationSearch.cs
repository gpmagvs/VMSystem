
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.MAP;
using System.Threading;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using VMSystem.VMS;

namespace VMSystem.AGV.TaskDispatch
{
    public class ParkStationSearch
    {

        internal IAGV agv { get; private set; }
        private List<IAGV> otherVehicles => VMSManager.AllAGV.FilterOutAGVFromCollection(this.agv).ToList();
        internal class ParkStationSearchResult
        {
            internal int Tag { get; set; } = -1;

            internal ACTION_TYPE ActionType { get; set; } = ACTION_TYPE.Charge;
        }


        internal async Task<ParkStationSearchResult> FindStationToPark(IAGV agv)
        {
            ParkStationSearchResult autoSearchChargeResult = new ParkStationSearchResult
            {
                Tag = -1,
                ActionType = ACTION_TYPE.Charge,
            };
            try
            {
                this.agv = agv;
                if (IsForceChargeState())
                    return autoSearchChargeResult;

                List<MapPoint> points = new List<MapPoint>();
                List<MapPoint> parkablePoints = StaMap.GetParkableStations().Where(pt => pt.StationType != MapPoint.STATION_TYPE.Normal).ToList();
                points.AddRange(parkablePoints);
                points.AddRange(StaMap.GetChargeableStations(agv).ToList());

                if (!points.Any() || !TryFindNearestParkableSpot(points, out MapPoint parkableSpot))
                    return autoSearchChargeResult;

                return new ParkStationSearchResult()
                {
                    Tag = parkableSpot.TagNumber,
                    ActionType = parkableSpot.IsChargeAble() ? ACTION_TYPE.Charge : ACTION_TYPE.Park,
                };
            }
            catch (Exception ex)
            {
                return autoSearchChargeResult;
            }
            finally
            {
            }
        }

        private bool IsAssignOrder(MapPoint pt)
        {
            var tagCollection = DatabaseCaches.TaskCaches.InCompletedTasks.SelectMany(order => new int[] { order.From_Station_Tag, order.To_Station_Tag })
                                                                           .Where(tag => tag > 0)
                                                                           .ToList();
            return tagCollection.Contains(pt.TagNumber);
        }

        private bool IsForceChargeState()
        {
            return (agv.batteryStatus <= IAGV.BATTERY_STATUS.MIDDLE_LOW);
        }

        private bool TryFindNearestParkableSpot(List<MapPoint> parkablePoints, out MapPoint parkableSpot)
        {
            parkableSpot = null;
            if (!parkablePoints.Any())
                return false;


            //filter :該站點不為禁用 且沒有任何車輛任務終點為該站 且沒有任何車輛正位於該站 且沒有任何車輛正位於該站的入口

            var parkablePointsFiltered = parkablePoints.Where(pt => pt.Enable)
                                                        .Where(pt => !IsAnyVehicleLocatin(pt))
                                                        .Where(pt => !IsAnyVehicleLocatinEntryPt(pt))
                                                        .Where(pt => !IsAnyVehicleOrderDestineAssigned(pt));

            if (!parkablePointsFiltered.Any())
                return false;

            //按照走行距離排序

            Dictionary<MapPoint, double> distanceOfVehicleMoveTo = parkablePointsFiltered.DistinctBy(p => p.TagNumber)
                                                                                         .ToDictionary(pt => pt, pt => CalculateDistanceFromVehicleToDestine(pt));

            distanceOfVehicleMoveTo = distanceOfVehicleMoveTo.OrderBy(pair => GetDistanceWithWeightByCarrierExistRack(pair.Key.TagNumber, pair.Value))
                                                             .ToDictionary(pair => pair.Key, pair => pair.Value);
            distanceOfVehicleMoveTo = distanceOfVehicleMoveTo.ToDictionary(p => p.Key, p => IsAssignOrder(p.Key) ? p.Value * 1000000 : p.Value);
            distanceOfVehicleMoveTo = distanceOfVehicleMoveTo.OrderBy(p => p.Value).ToDictionary(pair => pair.Key, pair => pair.Value);
            parkableSpot = distanceOfVehicleMoveTo.FirstOrDefault().Key;
            return parkableSpot != null;
        }

        private double CalculateDistanceFromVehicleToDestine(MapPoint pt)
        {
            PathFinder pf = new PathFinder();
            var pathInfo = pf.FindShortestPath(agv.currentMapPoint.TagNumber, pt.TagNumber, new PathFinder.PathFinderOption()
            {
                Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
                OnlyNormalPoint = false,
                Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
                //ConstrainTags = otherVehicles.Select(v => v.currentMapPoint.TagNumber).ToList()
            });
            if (pathInfo == null || pathInfo.tags.Count == 0)
                return double.MaxValue;
            double _weight = 1;
            return pathInfo.total_travel_distance * _weight;
        }

        /// <summary>
        /// return : 加上權重的距離量
        /// </summary>
        /// <param name="tag"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        private double GetDistanceWithWeightByCarrierExistRack(int tag, double distance)
        {
            return distance;
            //try get station info from database by tag
            using AGVSDatabase db = new AGVSDatabase();
            var stationsInfo = db.tables.StationStatus.Where(st => st.StationTag == tag.ToString());

            if (!stationsInfo.Any() || stationsInfo.All(st => string.IsNullOrEmpty(st.MaterialID)))
                return distance;

            return distance * 1000000000;
        }

        private bool IsAnyVehicleOrderDestineAssigned(MapPoint pt)
        {
            var orderAssigned = DatabaseCaches.TaskCaches.InCompletedTasks.Where(order => order.Action == ACTION_TYPE.Charge || order.Action == ACTION_TYPE.Park || order.Action == ACTION_TYPE.DeepCharge)
                                                       .Where(order => order.To_Station == pt.TagNumber.ToString())
                                                       .FirstOrDefault();
            return orderAssigned != null;
        }

        private bool IsAnyVehicleLocatin(MapPoint spot)
        {
            return otherVehicles.Any(vehicle => vehicle.currentMapPoint.TagNumber == spot.TagNumber);
        }
        private bool IsAnyVehicleLocatinEntryPt(MapPoint spot)
        {
            return spot.TargetNormalPoints().Any(pt => otherVehicles.Any(v => v.currentMapPoint.TagNumber == pt.TagNumber));
        }
    }
}

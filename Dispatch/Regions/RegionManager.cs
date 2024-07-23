using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.Notify;
using NLog;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.TrafficControl;
using VMSystem.TrafficControl.ConflicDetection;
using VMSystem.VMS;

namespace VMSystem.Dispatch.Regions
{
    public class RegionManager
    {


        public static Dictionary<MapRegion, clsRegionControlState> RegionsStates { get; set; } = new Dictionary<MapRegion, clsRegionControlState>();
        private static Logger logger;
        public class clsRegionControlState
        {
            public clsRegionControlState(MapRegion region)
            {
                Region = region;
                Task.Run(() => WatchEnterableState());
            }

            public readonly MapRegion Region;

            private bool _IsEnterable { get; set; } = false;

            public IEnumerable<IAGV> BookingRegionVehicles { get; private set; } = new List<IAGV>();

            public bool IsEnterable
            {
                get => _IsEnterable;
                private set
                {
                    if (_IsEnterable != value)
                    {
                        _IsEnterable = value;
                        if (_IsEnterable && WaitingForEnterVehicles.Any())
                        {
                            WaitingForEnterVehicles.FirstOrDefault().Value.allowEnterSignal.Set();
                        }
                        NotifyServiceHelper.INFO($"[{Region.Name}] 現在 {(_IsEnterable ? "可進入!" : "不可進入")}");
                    }
                }
            }

            private async Task WatchEnterableState()
            {
                while (true)
                {
                    await Task.Delay(10);
                    try
                    {
                        bool _IsEnterable()
                        {
                            BookingRegionVehicles = VMSManager.AllAGV.Where(agv => agv.currentMapPoint.GetRegion().Name == Region.Name || agv.NavigationState.NextNavigtionPoints.Any(pt => pt.GetRegion().Name == Region.Name))
                                                                     .ToList();
                            int inRegionOrGoThroughVehiclesCount = BookingRegionVehicles.Count();
                            return inRegionOrGoThroughVehiclesCount < Region.MaxVehicleCapacity;
                        }


                        IsEnterable = _IsEnterable();

                        if (!IsEnterable)
                        {
                            foreach (var agv in BookingRegionVehicles)
                            {
                                if (WaitingForEnterVehicles.TryGetValue(agv, out var state))
                                {
                                    state.allowEnterSignal.Set();
                                    WaitingForEnterVehicles.TryRemove(agv, out _);
                                }
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                    }

                }
            }


            public ConcurrentDictionary<IAGV, clsVehicleWaitingState> WaitingForEnterVehicles { get; set; } = new ConcurrentDictionary<IAGV, clsVehicleWaitingState>();

            public void JoinWaitingForEnter(IAGV agv, CancellationToken token)
            {
                if (WaitingForEnterVehicles.TryGetValue(agv, out var state))
                {
                    state.allowEnterSignal.Reset();
                    state.token = token;
                }
                else
                {
                    WaitingForEnterVehicles.TryAdd(agv, new clsVehicleWaitingState
                    {
                        startWaitTime = DateTime.Now,
                        allowEnterSignal = new ManualResetEvent(false),
                        token = token
                    });
                }
                if (IsEnterable)
                    WaitingForEnterVehicles[agv].allowEnterSignal.Set();
            }
            public class clsVehicleWaitingState
            {
                public DateTime startWaitTime { get; set; }
                public ManualResetEvent allowEnterSignal { get; set; }
                public CancellationToken token = new CancellationToken();
            }

        }

        public static void Initialze()
        {
            RegionsStates = StaMap.Map.Regions.ToDictionary(_region => _region, _region => new clsRegionControlState(_region));
        }


        public static IEnumerable<MapRegion> GetRegions()
        {
            return StaMap.Map.Regions;
        }

        internal static void RegistRegionToGo(IAGV vehicle, MapPoint finalMapPoint)
        {
            var agvReservedRegions = GetRegions().Where(region => region.ReserveRegionVehicles.Contains(vehicle.Name));
            foreach (var _region in agvReservedRegions)
            {
                _region.ReserveRegionVehicles.Remove(vehicle.Name);
            }
            var regionToGo = finalMapPoint.GetRegion();
            regionToGo.ReserveRegionVehicles.Add(vehicle.Name);
        }



        internal static void UpdateRegion(IAGV vehicle)
        {
            var agvPreviousRegions = GetRegions().Where(region => region.InRegionVehicles.Contains(vehicle.Name));
            foreach (var _region in agvPreviousRegions)
                _region.InRegionVehicles.Remove(vehicle.Name);
            vehicle.currentMapPoint.GetRegion().InRegionVehicles.Add(vehicle.Name);
        }

        internal static bool TryGetRegionMaxCapcity(MapPoint goalPoint, out int MaxVehicleCapacity)
        {
            MaxVehicleCapacity = goalPoint.GetRegion().MaxVehicleCapacity;
            return MaxVehicleCapacity != -1;
        }

        internal static bool TryGetRegionEntryPoints(MapPoint goalPoint, out IEnumerable<MapPoint> entryPoints)
        {
            entryPoints = goalPoint.GetRegion().EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag));
            return entryPoints.Any();
        }

        internal static bool TryGetRegionLeavePoints(MapPoint goalPoint, out IEnumerable<MapPoint> leavePoints)
        {
            leavePoints = goalPoint.GetRegion().LeavingTags.Select(tag => StaMap.GetPointByTagNumber(tag));
            return leavePoints.Any();
        }
        internal static IEnumerable<MapPoint> GetRegionEntryPoints(MapPoint goalPoint)
        {
            return GetRegionEntryPoints(goalPoint.GetRegion());
        }
        internal static IEnumerable<MapPoint> GetRegionEntryPoints(MapRegion nextRegion)
        {
            return nextRegion.EnteryTags.Select(tag => StaMap.GetPointByTagNumber(tag));

        }

        internal static async Task StartWaitToEntryRegion(IAGV agv, MapRegion region, CancellationToken token)
        {
            var regionGet = GetRegionControlState(region);
            if (regionGet == null)
                return;
            regionGet?.JoinWaitingForEnter(agv, token);
            await Task.Run(() =>
            {
                void HandleTaskCanceled(object sender, string taskName)
                {
                    try
                    {
                        regionGet.WaitingForEnterVehicles[agv].allowEnterSignal.Set();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Not Waiting.");
                    }
                    finally
                    {
                        agv.OnTaskCancel -= HandleTaskCanceled;
                    }
                };
                agv.OnTaskCancel += HandleTaskCanceled;
                regionGet.WaitingForEnterVehicles[agv].allowEnterSignal?.WaitOne();
                regionGet.WaitingForEnterVehicles.TryRemove(agv, out _);
            });
        }


        private static clsRegionControlState GetRegionControlState(MapRegion region)
        {
            KeyValuePair<MapRegion, clsRegionControlState> regionGet = RegionsStates.FirstOrDefault(kp => kp.Key.Name == region.Name);
            if (regionGet.Value == null)
                return null;
            return regionGet.Value;
        }

        internal static bool IsRegionEnterable(IAGV WannaEntryRegionVehicle, MapRegion regionQuery)
        {

            var regionGet = GetRegionControlState(regionQuery);
            if (regionGet == null)
                return true;

            bool isLastNavPathPointInRegion = WannaEntryRegionVehicle.NavigationState.NextNavigtionPoints.Any() && WannaEntryRegionVehicle.NavigationState.NextNavigtionPoints.Last().GetRegion().Name == regionQuery.Name;

            if (isLastNavPathPointInRegion || WannaEntryRegionVehicle.currentMapPoint.GetRegion().Name == regionQuery.Name)
            {
                return true;
            }

            return regionGet.IsEnterable;
        }

        internal static List<string> GetInRegionVehiclesNames(MapRegion regionQuery)
        {
            var regionGet = GetRegionControlState(regionQuery);
            if (regionGet == null)
                return new List<string>();

            return regionGet.BookingRegionVehicles.Select(agv => agv.Name).ToList();
        }

        internal static bool IsAGVWaitingRegion(IAGV Agv, MapRegion mapRegion)
        {
            try
            {
                if (!RegionsStates.Any())
                    return false;
                return RegionsStates.FirstOrDefault(kp => kp.Key.Name == mapRegion.Name).Value.WaitingForEnterVehicles.TryGetValue(Agv, out var state);
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        internal static void SetAGVNoWaitForEnteryRegion(IAGV Agv)
        {
            foreach (var region in RegionsStates)
            {
                if (region.Value.WaitingForEnterVehicles.TryRemove(Agv, out var state))
                {
                    state.allowEnterSignal.Set();
                    return;
                }

            }
        }
    }

    public static class Extensions
    {
        public static IEnumerable<MapRegion> GetRegions(this IEnumerable<MapPoint> path)
        {
            return path.Select(pt => pt.GetRegion())
                .DistinctBy(reg => reg.Name);
        }
    }
}

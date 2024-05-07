using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;

namespace VMSystem.Dispatch
{
    public static class ConflicRegionManager
    {

        public static Dictionary<MapRectangle, List<IAGV>> RegionsInWaiting = new Dictionary<MapRectangle, List<IAGV>>();

        public static void AddWaitingRegion(IAGV waitingVehicle, MapRectangle Region)
        {
            var exit = RegionsInWaiting.FirstOrDefault(rec => rec.Key.IsSameRegion(Region));
            if (exit.Key == null)
            {
                RegionsInWaiting.Add(Region, new List<IAGV>() { waitingVehicle });
            }
            else
            {
                if (!RegionsInWaiting[exit.Key].Any(v => v.Name == waitingVehicle.Name))
                {
                    RegionsInWaiting[exit.Key].Add(waitingVehicle);
                }
            }
        }

    }

    public static class ConflicRegionManagerExtention
    {
        public static bool IsSameRegion(this MapRectangle region1, MapRectangle region2)
        {
            return region1.StartPointTag.TagNumber == region2.StartPointTag.TagNumber
                && region1.EndPointTag.TagNumber == region2.EndPointTag.TagNumber;
        }
    }
}

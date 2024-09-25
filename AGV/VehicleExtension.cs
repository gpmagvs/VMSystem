using AGVSystemCommonNet6.MAP;

namespace VMSystem.AGV
{
    public static class VehicleExtension
    {
        /// <summary>
        /// 取得被設定為不可停車的Tag Numbers
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static List<int> GetCanNotReachTags(this IAGV vehicle)
        {
            return vehicle.model == AGVSystemCommonNet6.clsEnums.AGV_TYPE.SUBMERGED_SHIELD ?
                                                 StaMap.Map.TagNoStopOfSubmarineAGV : StaMap.Map.TagNoStopOfForkAGV;
        }

        /// <summary>
        /// 取得被設定為不可停車的地圖點位
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public static List<MapPoint> GetCanNotReachMapPoints(this IAGV vehicle)
        {
            return vehicle.GetCanNotReachTags().Select(tag => StaMap.GetPointByTagNumber(tag)).ToList();
        }
    }
}

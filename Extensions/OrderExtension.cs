using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.Extensions
{
    public static class OrderExtension
    {
        /// <summary>
        /// 是否為充電任務訂單(包含深充)
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        public static bool IsChargeOrder(this clsTaskDto order)
        {
            if (order == null)
                return false;
            return order.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Charge || order.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.DeepCharge;
        }


        public static bool IsSourceZonePort(this clsTaskDto order)
        {
            if (order == null) return false;
            MapPoint mpt = order.From_Station_Tag.GetMapPoint();
            if (mpt == null) return false;
            return _IsOrderGoalBelongZonePort(mpt, order.GetFromSlotInt());
        }

        public static bool IsSourceEQPort(this clsTaskDto order)
        {
            if (order == null) return false;
            MapPoint mpt = order.From_Station_Tag.GetMapPoint();
            if (mpt == null) return false;
            return _IsOrderGoalBelongEQPort(mpt,order.GetFromSlotInt());
        }

        public static bool IsDestineZonePort(this clsTaskDto order)
        {
            if (order == null) return false;
            MapPoint mpt = order.To_Station_Tag.GetMapPoint();
            if (mpt == null) return false;
            return _IsOrderGoalBelongZonePort(mpt, order.GetToSlotInt());
        }
        public static bool IsDestineEQPort(this clsTaskDto order)
        {
            if (order == null) return false;
            MapPoint mpt = order.To_Station_Tag.GetMapPoint();
            if (mpt == null) return false;
            return _IsOrderGoalBelongEQPort(mpt, order.GetToSlotInt());
        }

        private static bool _IsOrderGoalBelongZonePort(MapPoint mpt, int slot)
        {
            List<MapPoint.STATION_TYPE> bufferTypes = new() {
                 MapPoint.STATION_TYPE.Buffer,
                 MapPoint.STATION_TYPE.Buffer_EQ,
                 MapPoint.STATION_TYPE.Charge_Buffer
            };
            if (mpt == null) return false;
            if (!bufferTypes.Contains(mpt.StationType)) return false;
            if (mpt.StationType == MapPoint.STATION_TYPE.Buffer_EQ && slot == 0) return false;
            return true;

        }


        private static bool _IsOrderGoalBelongEQPort(MapPoint mpt, int slot)
        {
            List<MapPoint.STATION_TYPE> EQPortTypes = new() {
                 MapPoint.STATION_TYPE.Buffer_EQ,
                  MapPoint.STATION_TYPE.EQ
            };
            if (mpt == null) return false;
            if (!EQPortTypes.Contains(mpt.StationType)) return false;
            if (mpt.StationType == MapPoint.STATION_TYPE.Buffer_EQ && slot > 0) return false;
            return true;

        }
    }
}

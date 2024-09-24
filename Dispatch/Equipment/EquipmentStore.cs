using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;

namespace VMSystem.Dispatch.Equipment
{
    public class EquipmentStore
    {
        public static Dictionary<string, clsEqInformation> EquipmentInfo = new Dictionary<string, clsEqInformation>();

        internal static clsEnums.AGV_TYPE GetEQAcceptAGVType(int tagNumber, int slotHeight = 0)
        {

            if (slotHeight > 0)
            {
                return clsEnums.AGV_TYPE.FORK;
            }

            clsEqInformation eqInfo = EquipmentInfo.Values.FirstOrDefault(eq => eq.Tag == tagNumber);
            if (eqInfo != null)
            {
                return eqInfo.Accept_AGV_Type;
            }

            MapPoint mapPoint = StaMap.GetPointByTagNumber(tagNumber);

            if (mapPoint.StationType == MapPoint.STATION_TYPE.Buffer)
                return clsEnums.AGV_TYPE.FORK;

            return clsEnums.AGV_TYPE.Null;
        }

        internal static List<int> GetTransferTags(int stationTag)
        {
            clsEqInformation eqInfo = EquipmentInfo.Values.FirstOrDefault(eq => eq.Tag == stationTag);
            if (eqInfo == null)
            {
                return new();
            }

            return eqInfo.AllowTransferToTags;

        }
    }
}

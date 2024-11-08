using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace VMSystem.Dispatch.Equipment
{
    public class EquipmentStore
    {
        public static Dictionary<string, clsEqInformation> EquipmentInfo = new Dictionary<string, clsEqInformation>();

        internal static clsEnums.AGV_TYPE GetEQAcceptAGVType(int tagNumber, int slotHeight = 0)
        {
            STATION_TYPE stationTpye = StaMap.GetPointByTagNumber(tagNumber).StationType;

            if (slotHeight > 0 || stationTpye == STATION_TYPE.Buffer || stationTpye == STATION_TYPE.Charge_Buffer)
            {
                return clsEnums.AGV_TYPE.FORK;
            }

            clsEqInformation eqInfo = EquipmentInfo.Values.FirstOrDefault(eq => eq.Tag == tagNumber);
            if (eqInfo != null)
            {
                return eqInfo.Accept_AGV_Type;
            }
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

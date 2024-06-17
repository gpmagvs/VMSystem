using AGVSystemCommonNet6;

namespace VMSystem.Dispatch.Equipment
{
    public class EquipmentStore
    {
        public static Dictionary<string, clsEqInformation> EquipmentInfo = new Dictionary<string, clsEqInformation>();

        internal static clsEnums.AGV_TYPE GetEQAcceptAGVType(int tagNumber)
        {
            clsEqInformation eqInfo = EquipmentInfo.Values.FirstOrDefault(eq => eq.Tag == tagNumber);
            if (eqInfo != null)
            {
                return eqInfo.Accept_AGV_Type;
            }
            else
            {
                return clsEnums.AGV_TYPE.Null;
            }
        }
    }
}

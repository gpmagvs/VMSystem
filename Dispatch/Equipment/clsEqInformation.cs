using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.Dispatch.Equipment
{
    public class clsEqInformation
    {
        public string EqName { get; set; } = "";
        public int Tag { get; set; } = 0;
        public AGV_TYPE Accept_AGV_Type { get; set; } = AGV_TYPE.SUBMERGED_SHIELD;

    }
}

using System.Diagnostics;
using VMSystem.AGV;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{
    public class YunTechAgvVMS : VMSAbstract
    {
        public override VMS_GROUP Model { get; set; } = VMS_GROUP.YUNTECH_FORK;
        public YunTechAgvVMS(List<clsYunTechAGV> yuntech_fork_agvList)
        {
            AGVList = yuntech_fork_agvList.ToDictionary(agv => agv.Name, agv => (IAGV)agv);
        }

        public YunTechAgvVMS(List<IAGV> AGVList) : base(AGVList)
        {
        }

         
    }
}

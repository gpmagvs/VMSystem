using System.Diagnostics;
using VMSystem.AGV;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{
    public class GPMSubmarine_ShieldVMS : VMSAbstract
    {
        public override VMS_GROUP Model { get; set; } = VMS_GROUP.GPM_SUBMARINE_SHIELD;
        public GPMSubmarine_ShieldVMS(List<clsGPMSubmarine_Shield> gpm_submarine_shieldList)
        {
            AGVList = gpm_submarine_shieldList.ToDictionary(agv => agv.Name, agv => (IAGV)agv);
        }

        public GPMSubmarine_ShieldVMS(List<IAGV> AGVList) : base(AGVList)
        {
        }

         
    }
}

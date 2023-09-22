using AGVSystemCommonNet6;
using VMSystem.AGV;

namespace VMSystem.VMS
{
    public class GPMInspectionAGVVMS : VMSAbstract
    {
        public GPMInspectionAGVVMS(List<IAGV> AGVList) : base(AGVList)
        {
        }
        public GPMInspectionAGVVMS(List<clsGPMInspectionAGV> yuntech_fork_agvList)
        {
            AGVList = yuntech_fork_agvList.ToDictionary(agv => agv.Name, agv => (IAGV)agv);
        }

        public override clsEnums.VMS_GROUP Model { get; set; } = clsEnums.VMS_GROUP.GPM_INSPECTION_AGV;


    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    public class clsGPMInspectionAGV : clsGPMForkAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_INSPECTION_AGV;
        public override AGV_TYPE model { get; set; } = AGV_TYPE.INSPECTION_AGV;
        public clsGPMInspectionAGV(string name, clsAGVOptions options) : base(name, options)
        {
            LOG.INFO($"AGV {name} Create. MODEL={model} ");
        }
    }
}

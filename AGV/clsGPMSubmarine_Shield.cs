using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.VMS;

namespace VMSystem.AGV
{
    public class clsGPMSubmarine_Shield : clsGPMForkAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_SUBMARINE_SHIELD;
        public override clsEnums.AGV_MODEL model { get; set; } = clsEnums.AGV_MODEL.SUBMERGED_SHIELD;
        public clsGPMSubmarine_Shield(string name, clsAGVOptions connections) : base(name, connections)
        {
            LOG.INFO($"AGV {name} Create. MODEL={model} ");
        }

        public override Task<object> GetAGVStateFromDB()
        {
            return base.GetAGVStateFromDB();
        }
    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    public class clsGPMSubmarine_Shield : clsAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_SUBMARINE_SHIELD;
        public override AGV_TYPE model { get; set; } = AGV_TYPE.SUBMERGED_SHIELD;
        public clsGPMSubmarine_Shield(string name, clsAGVOptions connections, AGVSDbContext dbContext) : base(name, connections, dbContext)
        {
        }

    }
}

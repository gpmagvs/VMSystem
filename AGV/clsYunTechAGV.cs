using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    public class clsYunTechAGV : clsGPMForkAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.YUNTECH_FORK;
        public override AGV_TYPE model { get; set; } = AGV_TYPE.FORK;
        public clsYunTechAGV(string name, clsAGVOptions connections) : base(name, connections)
        {
            LOG.INFO($"AGV {name} Create. MODEL={model} ");
        }

        public override Task<object> GetAGVStateFromDB()
        {
            return base.GetAGVStateFromDB();
        }
    }
}

using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;

namespace VMSystem.AGV
{
    public class clsYunTechAGV : clsGPMForkAGV
    {
        public override clsEnums.AGV_MODEL model { get; set; } = clsEnums.AGV_MODEL.YUNTECH_FORK_AGV;
        public clsYunTechAGV(string name, clsAGVOptions connections, int initTag = 51, bool simulationMode = false) : base(name, connections, initTag, simulationMode)
        {
            LOG.INFO($"AGV {name} Create. MODEL={model} ");
        }

        public override Task<object> GetAGVState()
        {
            return base.GetAGVState();
        }
    }
}

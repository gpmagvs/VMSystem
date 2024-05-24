using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{
    public class GPMForkAgvVMS : VMSAbstract
    {
        public override VMS_GROUP Model { get; set; } = VMS_GROUP.GPM_FORK;
        public GPMForkAgvVMS(List<clsAGV> _agvList)
        {
            AGVList = _agvList.ToDictionary(agv=>agv.Name, agv=>(IAGV) agv );
        }
        public GPMForkAgvVMS(List<IAGV> AGVList) : base(AGVList)
        {

            this.AGVList = AGVList.ToDictionary(i => i.Name, i => i);
        }

        public GPMForkAgvVMS()
        {
        }

    }
}

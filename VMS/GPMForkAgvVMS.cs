using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV;

namespace VMSystem.VMS
{
    public class GPMForkAgvVMS : VMSAbstract
    {
        public override VMS_MODELS Model { get; set; } = VMS_MODELS.GPM_FORK;
        public GPMForkAgvVMS(List<clsGPMForkAGV> _agvList)
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

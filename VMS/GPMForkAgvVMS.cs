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
            AGVList = new Dictionary<string, IAGV>()
             {
                 {"AGV_1",new clsGPMForkAGV("AGV_1", new clsAGVOptions { HostIP = "192.168.0.101", HostPort = 7025 },9,simulationMode:true) },
                 {"AGV_2",new clsGPMForkAGV("AGV_2", new clsAGVOptions { HostIP = "192.168.0.102", HostPort = 7025 },21,simulationMode:true )},
                 //{"AGV_3",new clsGPMForkAGV("AGV_3", new clsConnections { HostIP = "127.0.0.1", HostPort = 7027 },61,simulationMode:true ) },
                 //{"AGV_4",new clsGPMForkAGV("AGV_4", new clsConnections { HostIP = "127.0.0.1", HostPort = 7028 },47,simulationMode:true ) },
             };
        }

    }
}

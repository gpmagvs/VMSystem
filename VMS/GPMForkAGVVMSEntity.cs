using AGVSystemCommonNet6.DATABASE;
using VMSystem.AGV;

namespace VMSystem.VMS
{
    public class GPMForkAGVVMSEntity : IVms
    {
        public VMS_MODELS Model { get; set; } = VMS_MODELS.GPM_FORK;
        public Dictionary<string, IAGV> AGVList { get; set; }
        AGVStatusDBHelper AGVStatusDBHelper { get; set; } = new AGVStatusDBHelper();
        public GPMForkAGVVMSEntity(List<IAGV> AGVList)
        {
            this.AGVList = AGVList.ToDictionary(i => i.Name, i => i);

            foreach (IAGV agv in AGVList)
            {
                AGVStatusDBHelper.Add(new AGVSystemCommonNet6.clsAGVStateDto
                {
                    AGV_Name = agv.Name,
                    Model = AGVSystemCommonNet6.clsEnums.AGV_MODEL.FORK_AGV,
                });
            }
        }
        public GPMForkAGVVMSEntity()
        {
            AGVList = new Dictionary<string, IAGV>()
             {
                 {"AGV_1",new clsGPMForkAGV("AGV_1", new clsConnections { HostIP = "192.168.0.101", HostPort = 7025 },simulationMode:false) },
                 {"AGV_2",new clsGPMForkAGV("AGV_2", new clsConnections { HostIP = "192.168.0.102", HostPort = 7025 },simulationMode:false )},
                 //{"AGV_3",new clsGPMForkAGV("AGV_3", new clsConnections { HostIP = "127.0.0.1", HostPort = 7027 },61,simulationMode:true ) },
                 //{"AGV_4",new clsGPMForkAGV("AGV_4", new clsConnections { HostIP = "127.0.0.1", HostPort = 7028 },47,simulationMode:true ) },
                 //{"AGV_5",new clsGPMForkAGV("AGV_5", new clsConnections { HostIP = "127.0.0.1", HostPort = 7029 },71,simulationMode:true )},
                 //{"AGV_6",new clsGPMForkAGV("AGV_6", new clsConnections { HostIP = "127.0.0.1", HostPort = 7030 },11,simulationMode:true ) },
                 //{"AGV_7",new clsGPMForkAGV("AGV_7", new clsConnections { HostIP = "127.0.0.1", HostPort = 7031 },9,simulationMode:true ) },
                 //{"AGV_8",new clsGPMForkAGV("AGV_8", new clsConnections { HostIP = "127.0.0.1", HostPort = 7032 },43,simulationMode:true ) },
             };

            foreach (KeyValuePair<string, IAGV> agv in AGVList)
            {
                AGVStatusDBHelper.Add(new AGVSystemCommonNet6.clsAGVStateDto
                {
                    AGV_Name = agv.Key,
                    Model = AGVSystemCommonNet6.clsEnums.AGV_MODEL.FORK_AGV,
                });
            }

        }
    }
}

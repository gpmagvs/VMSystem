namespace VMSystem.AGV
{
    public class clsAGVOptions
    {
        public enum PROTOCOL
        {
            TCP,
            RESTFulAPI,
        }

        public string HostIP { get; set; }
        public int HostPort { get; set; }
        public PROTOCOL Protocol { get; set; } 

        public bool Simulation { get; set; }

        public int InitTag { get; set; } = 1;

        public bool Enabled { get; set; } = true;
        /// <summary>
        /// 車身長度
        /// <remark>
        /// 單位:cm
        /// </remark>
        /// </summary>
        public double VehicleLength { get; set; } = 145.0;
    }
}

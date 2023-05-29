namespace VMSystem.AGV
{
    public class clsConnections
    {
        public enum PROTOCOL
        {
            RESTFulAPI,
            TCP,
        }

        public string HostIP { get; set; }
        public int HostPort { get; set; }

    }
}

namespace VMSystem.AGV.Update
{
    public static class AGVProgramUpdateHelper
    {
        public class clsOTAInfo
        {
            public string fileUrl { get; set; } = "";
            public string version { get; set; } = "";
            public DateTime createTime { get; set; } = DateTime.MinValue;
        }
        private static string VMSHostUrl => AGVSystemCommonNet6.Configuration.AGVSConfigulator.SysConfigs.VMSHost;

    }
}

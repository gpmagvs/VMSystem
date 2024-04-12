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
        private static string UpdateFilesFolder => AGVSystemCommonNet6.Configuration.AGVSConfigulator.SysConfigs.AGVUpdateFileFolder;
        private static string VMSHostUrl => AGVSystemCommonNet6.Configuration.AGVSConfigulator.SysConfigs.VMSHost;

        /// <summary>
        /// 取得最新的更新包
        /// </summary>
        public static clsOTAInfo GetNewestUpdateFile()
        {
            var files = GetUpdateFiles();

            if (files.Count == 0)
            {
                return new clsOTAInfo();
            }
            files = files.OrderByDescending(file => (new FileInfo(file)).CreationTime).ToList();
            var newestFile = files.First();

            var filename = Path.GetFileName(newestFile);
            var createTime = new FileInfo(newestFile).CreationTime;

            return new clsOTAInfo
            {
                createTime = createTime,
                fileUrl = $"{VMSHostUrl}/AGVUpdateFiles/{filename}"
            };
        }

        private static List<string> GetUpdateFiles()
        {
            var files = Directory.GetFiles(UpdateFilesFolder);
            return files.Where(file => _isCompressFile(file)).ToList();

            bool _isCompressFile(string filePath)
            {
                List<string> support_extensions = new List<string>() { ".7z", ".zip", ".tar" };

                if (!File.Exists(filePath))
                {
                    return false;
                }
                return support_extensions.Contains((new FileInfo(filePath)).Extension);
            }
        }
    }
}

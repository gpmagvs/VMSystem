namespace VMSystem.Dispatch.Configurations
{
    public class EQStationDedicatedConfiguration
    {
        public readonly string FilePath;
        public EQStationDedicatedConfiguration(string _filePath)
        {
            FilePath = _filePath;
            Load();
        }

        public List<EQStationDedicatedSetting> EQStationDedicatedSettings { get; set; } = new();


        internal void Load()
        {
            if (System.IO.File.Exists(FilePath))
            {
                string json = System.IO.File.ReadAllText(FilePath);
                EQStationDedicatedSettings = Newtonsoft.Json.JsonConvert.DeserializeObject<List<EQStationDedicatedSetting>>(json);
            }
            Save();
        }

        internal void Save()
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(EQStationDedicatedSettings, Newtonsoft.Json.Formatting.Indented);
            System.IO.File.WriteAllText(FilePath, json);
        }

    }


    public class EQStationDedicatedSetting
    {
        public int tag { get; set; }
        public List<int> dedicatedStations { get; set; } = new();

    }
}

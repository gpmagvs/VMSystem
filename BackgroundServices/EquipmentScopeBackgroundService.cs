
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Notify;
using Newtonsoft.Json;
using System.IO;
using VMSystem.Dispatch.Equipment;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.BackgroundServices
{
    public class EquipmentScopeBackgroundService : BackgroundService
    {
        FileSystemWatcher fileSystemWatcher;
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            UpdateEQInfoFromConfigFile(Path.Combine(AGVSConfigulator.SysConfigs.PATHES_STORE[SystemConfigs.PATH_ENUMS.EQ_CONFIGS_FOLDER_PATH], "EQConfigs.json"));
            StartEquipmentConfigFileChangedFileWatch();
        }


        private void GetEquipmentConfiguration()
        {

        }

        void StartEquipmentConfigFileChangedFileWatch()
        {
            string eqConfigFolder = AGVSConfigulator.SysConfigs.PATHES_STORE[SystemConfigs.PATH_ENUMS.EQ_CONFIGS_FOLDER_PATH];
            string eqConfigFileName = "EQConfigs.json";
            fileSystemWatcher = new FileSystemWatcher(eqConfigFolder, eqConfigFileName);
            fileSystemWatcher.Changed += _FileSystemWatcher_Changed;
            fileSystemWatcher.EnableRaisingEvents = true;


        }
        async void _FileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            try
            {
                fileSystemWatcher.EnableRaisingEvents = false;
                await Task.Delay(200);
                // copy changed file to temp folder 
                string tempFile = Path.Combine(Path.GetTempPath(), "EQConfigs.json");
                File.Copy(e.FullPath, tempFile, true);
                //read the temp file and use Jsonconvert to convert to EquipmentConfiguration
                UpdateEQInfoFromConfigFile(tempFile);
            }
            catch (Exception ex)
            {
                LOG.ERROR(ex.Message);
            }
            finally
            {
                fileSystemWatcher.EnableRaisingEvents = true;
            }

        }

        private static void UpdateEQInfoFromConfigFile(string tempFile)
        {
            string eqConfig = File.ReadAllText(tempFile);
            Dictionary<string, Dictionary<string, object>> equipmentConfiguration = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(eqConfig);
            EquipmentStore.EquipmentInfo = equipmentConfiguration.ToDictionary(
                keypair => keypair.Key,
                keypair =>
                {
                    string eqName = keypair.Key;
                    int tag = int.Parse(keypair.Value["TagID"].ToString());
                    int Accept_AGV_Type = int.Parse(keypair.Value["Accept_AGV_Type"].ToString());

                    return new clsEqInformation()
                    {
                        EqName = eqName,
                        Tag = tag,
                        Accept_AGV_Type = GetAcceptAGVType(Accept_AGV_Type)
                    };

                    AGV_TYPE GetAcceptAGVType(int typeInt)
                    {
                        switch (typeInt)
                        {
                            case 0:
                                return AGV_TYPE.Any;
                            case 1:
                                return AGV_TYPE.FORK;
                            case 2:
                                return AGV_TYPE.SUBMERGED_SHIELD;
                            default:
                                return AGV_TYPE.Any;
                        }
                    }

                });

            NotifyServiceHelper.SUCCESS($"設備設定資料已更新!");
        }
    }
}

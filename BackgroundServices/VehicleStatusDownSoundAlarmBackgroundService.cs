
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.Microservices.AudioPlay;

namespace VMSystem.BackgroundServices
{
    public class VehicleStatusDownSoundAlarmBackgroundService : BackgroundService
    {

        private enum VEHICLES_STATUS_DOWN_STATE
        {
            ALL_NOT_DOWN,
            SOME_DOWN,
            ALARM_AUDIO_PLAYING
        }

        private VEHICLES_STATUS_DOWN_STATE vehiclesDownState = VEHICLES_STATUS_DOWN_STATE.ALL_NOT_DOWN;
        public static string AGVDownAudioName => Path.Combine(AGVSConfigulator.ConfigsFilesFolder, "Sounds/agv_status_down_alarm.wav");
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(1).ContinueWith(async tk =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    switch (vehiclesDownState)
                    {
                        case VEHICLES_STATUS_DOWN_STATE.ALL_NOT_DOWN:
                            // 檢查所有AGV是否有一台或多台AGV的狀態為DOWN
                            if (VehicleStateService.AGVStatueDtoStored.Values.Any(v => v.MainStatus == AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN))
                                vehiclesDownState = VEHICLES_STATUS_DOWN_STATE.SOME_DOWN;
                            else
                                vehiclesDownState = VEHICLES_STATUS_DOWN_STATE.ALL_NOT_DOWN;
                            break;
                        case VEHICLES_STATUS_DOWN_STATE.SOME_DOWN:
                            AudioPlayService.AddAudioToPlayQueue(AGVDownAudioName);
                            vehiclesDownState = VEHICLES_STATUS_DOWN_STATE.ALARM_AUDIO_PLAYING;
                            break;
                        case VEHICLES_STATUS_DOWN_STATE.ALARM_AUDIO_PLAYING:
                            // 檢查所有AGV皆不是DOWN狀態
                            if (VehicleStateService.AGVStatueDtoStored.Values.All(v => v.MainStatus != AGVSystemCommonNet6.clsEnums.MAIN_STATUS.DOWN))
                            {
                                AudioPlayService.RemoveAudioFromQueue(AGVDownAudioName);
                                vehiclesDownState = VEHICLES_STATUS_DOWN_STATE.ALL_NOT_DOWN;
                            }
                            else
                                vehiclesDownState = VEHICLES_STATUS_DOWN_STATE.ALARM_AUDIO_PLAYING;
                            break;
                        default:
                            break;
                    }

                    await Task.Delay(200);
                }
            });
        }
    }
}

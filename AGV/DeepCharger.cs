using AGVSystemCommonNet6.Alarm;
using System.Diagnostics;
using System.Text;
using static AGVSystemCommonNet6.GPMRosMessageNet.Services.EquipmentStateRequest;

namespace VMSystem.AGV
{
    public class DeepCharger
    {
        public DeepCharger(IAGV agv)
        {
            this.Agv = agv;
        }

        internal static event EventHandler<DeepChargeRequsetDto> OnDeepChargeRequestRaised;

        private readonly IAGV Agv;
        private bool _IsDeepCharging = false;
        private bool _DeepChargeRequesting = false;
        private Stopwatch _DeepChargeStopWatcher = new Stopwatch();
        private bool _AutoCreateDeepChargeTask => false;


        /// <summary>
        /// 車載發起深充請求
        /// </summary>
        public bool DeepChargeRequesting
        {
            get => _DeepChargeRequesting;
            set
            {
                if (_DeepChargeRequesting != value)
                {
                    _DeepChargeRequesting = value;

                    if (_DeepChargeRequesting)
                    {
                        AlarmManagerCenter.AddAlarmAsync(ALARMS.AGV_Battery_SOC_Distortion, source: ALARM_SOURCE.AGVS, level: ALARM_LEVEL.WARNING, Equipment_Name: Agv.Name, Agv.currentMapPoint.Graph.Display);

                        if (_AutoCreateDeepChargeTask)
                        {
                            DeepChargeRequsetDto requestDto = new DeepChargeRequsetDto(this.Agv);
                            OnDeepChargeRequestRaised?.Invoke(this, requestDto);
                            if (requestDto.Accept)
                            {
                                //
                            }
                        }
                    }
                }
            }
        }

        public DeepChargeRecorder? Recorder { get; internal set; }

        public void StartDeepCharging()
        {
            _IsDeepCharging = true;
            _DeepChargeStopWatcher.Restart();
            Recorder?.StartDeepCharge();
            AlarmManagerCenter.SetAlarmCheckedAsync(Agv.Name, ALARMS.AGV_Battery_SOC_Distortion);
        }

        public void StopDeepCharging(bool isAuto)
        {
            _IsDeepCharging = false;
            _DeepChargeStopWatcher.Stop();
            var endBy = isAuto ? AGVSystemCommonNet6.Equipment.AGV.DeepChargeRecord.DEEP_CHARGE_TRIGGER_MOMENT.AUTO : AGVSystemCommonNet6.Equipment.AGV.DeepChargeRecord.DEEP_CHARGE_TRIGGER_MOMENT.MANUAL;
            Recorder?.EndDeepCharge(endBy);
            Recorder = null;
        }

        public bool GetIsDeepCharging()
        {
            return _IsDeepCharging;
        }


        public class DeepChargeRequsetDto
        {
            public readonly IAGV Agv;
            public bool Accept { get; set; } = false;
            public string Message { get; set; } = "";
            public DeepChargeRequsetDto(IAGV Agv)
            {
                this.Agv = Agv;
            }
        }
    }
}

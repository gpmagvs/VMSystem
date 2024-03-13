using AGVSystemCommonNet6;
using static System.Collections.Specialized.BitVector32;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using AGVSystemCommonNet6.AGVDispatch.Model;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.Vehicle_Control.CarComponent;
using System.Xml.Linq;
using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using System.Text;

namespace VMSystem.AGV
{
    public partial class clsAGVSimulation
    {
        internal clsAGVSimulationParameters parameters = new clsAGVSimulationParameters();
        public class clsAGVSimulationParameters
        {
            /// <summary>
            /// 真實走行速度(m/s)
            /// </summary>
            public double MoveSpeedRatio { get; set; } = 1;

            /// <summary>
            /// 色帶速度
            /// </summary>
            public double TapMoveSpeedRatio { get; set; } = 0.3;
            /// <summary>
            /// 真實旋轉速度(度/秒)
            /// </summary>
            public double RotationSpeed { get; set; } = 15;

            public double ForkLifterSpeed { get; set; }

            public double SpeedUpRate { get; set; } = 1;

            public double BatteryChargeSpeed { get; set; } = 9;
            public double BatteryUsed_Run { get; set; } = 0.1;

            public double WorkingTime { get; set; } = 4;//秒

            internal double WorkingTimeAwait => WorkingTime / SpeedUpRate;
        }
    }
}

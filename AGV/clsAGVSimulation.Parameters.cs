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

            /// <summary>
            /// 模擬加速倍率
            /// </summary>
            public double SpeedUpRate { get; set; } = 4;
            /// <summary>
            /// 充電速度
            /// </summary>
            public double BatteryChargeSpeed { get; set; } = 9;
            public double BatteryUsed_Run { get; set; } = 0.1;

            /// <summary>
            /// 設備作業時間(AGV進入停在設備內後，等待設備作業完成的花費時間)
            /// </summary>
            public double EQWorkingTime { get; set; } = 4;//秒

            internal double WorkingTimeAwait => EQWorkingTime / SpeedUpRate;
        }
    }
}

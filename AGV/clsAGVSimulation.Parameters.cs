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
            /// 走行速度(m/s)
            /// </summary>
            public double MoveSpeedRatio { get; set; } = 2;
            public double RotationSpeed { get; set; }

            public double ForkLifterSpeed { get; set; }

            public double SpeedUpRate { get; set; } = 4;
        }
    }
}

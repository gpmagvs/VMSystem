using AGVSystemCommonNet6;
using static System.Collections.Specialized.BitVector32;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using AGVSystemCommonNet6.AGVDispatch.Model;
using VMSystem.TrafficControl;
using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.Abstracts.CarComponent;
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
            public double MoveSpeed { get; set; } = 0.1;
            public double RotationSpeed { get; set; }

            public double ForkLifterSpeed { get; set; }
        }
    }
}

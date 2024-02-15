using AGVSystemCommonNet6.DATABASE.Helpers;
using Microsoft.Extensions.Options;
using VMSystem.AGV;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.VMS
{

    /// <summary>
    /// 收集車輛狀態
    /// </summary>
    public abstract class VMSAbstract
    {
        protected AGVStatusDBHelper AGVStatusDBHelper { get; set; } = new AGVStatusDBHelper();
        public VMSAbstract() { }
        public VMSAbstract(List<IAGV> AGVList)
        {

        }

        /// <summary>
        /// 所管理之車輛的類型
        /// </summary>
        public abstract VMS_GROUP Model { get; set; }


        /// <summary>
        /// 保存各車輛的狀態
        /// </summary>
        public Dictionary<string, IAGV> AGVList { get; set; }

        internal void StartAGVs()
        {
            AGVList.Values.ToList().ForEach((agv) =>
            {
                agv.Run();
             
            });
        }
    }
}

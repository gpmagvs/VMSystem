using VMSystem.AGV;

namespace VMSystem.VMS
{

    public enum VMS_MODELS
    {
        GPM_FORK,
        GPM_SUBMARINE_SHIELD,
        YUNTECH_FORK
    }

    /// <summary>
    /// 收集車輛狀態
    /// </summary>
    public interface IVms
    {
        /// <summary>
        /// 所管理之車輛的類型
        /// </summary>
        VMS_MODELS Model { get; set; }
        /// <summary>
        /// 保存各車輛的狀態
        /// </summary>
        Dictionary<string, IAGV> AGVList { get; set; }

    }
}

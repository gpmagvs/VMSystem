﻿namespace VMSystem.AGV.TaskDispatch.OrderHandler.OrderTransferSpace
{
    /// <summary>
    /// 訂單轉移功能配置參數
    /// </summary>
    public class OrderTransferConfiguration
    {
        /// <summary>
        /// 功能開關
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 一筆訂單最多可以轉移幾次
        /// </summary>
        public int MaxTransferTimes { get; set; } = 1;

    }
}

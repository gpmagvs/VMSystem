using AGVSystemCommonNet6.AGVDispatch;

namespace VMSystem.Extensions
{
    public static class OrderExtension
    {
        /// <summary>
        /// 是否為充電任務訂單(包含深充)
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        public static bool IsChargeOrder(this clsTaskDto order)
        {
            if (order == null)
                return false;
            return order.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.Charge || order.Action == AGVSystemCommonNet6.AGVDispatch.Messages.ACTION_TYPE.DeepCharge;
        }
    }
}

using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Exceptions;
using System.Runtime.Serialization;

namespace VMSystem.AGV.TaskDispatch.Exceptions
{
    [Serializable]
    internal class AGVRejectTaskException : VMSExceptionAbstract
    {
        private TASK_DOWNLOAD_RETURN_CODES returnCode;
        public override ALARMS Alarm_Code { get; set; } = ALARMS.REGIST_REGIONS_TO_PARTS_SYSTEM_FAIL;
    }

    [Serializable]
    internal class RegionNotEnterableException : VMSExceptionAbstract
    {
        private TASK_DOWNLOAD_RETURN_CODES returnCode;
        public override ALARMS Alarm_Code { get; set; } = ALARMS.REGION_NOT_ENTERABLE;
    }
}
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

        public AGVRejectTaskException(TASK_DOWNLOAD_RETURN_CODES returnCode)
        {
            this.returnCode = returnCode;
            //TODO轉換異常碼
            switch (returnCode)
            {
                case TASK_DOWNLOAD_RETURN_CODES.OK:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_STATUS_DOWN:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_NOT_ON_TAG:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.WORKSTATION_NOT_SETTING_YET:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_BATTERY_LOW_LEVEL:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_CANNOT_GO_TO_WORKSTATION_WITH_NORMAL_MOVE_ACTION:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_CANNOT_EXECUTE_NORMAL_MOVE_ACTION_IN_NON_NORMAL_POINT:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.AGV_CANNOT_EXECUTE_TASK_WHEN_WORKING_AT_WORKSTATION:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_DATA_ILLEAGAL:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.SYSTEM_EXCEPTION:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.NO_PATH_FOR_NAVIGATION:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.OK_AGV_ALREADY_THERE:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.TASK_CANCEL:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.TASK_DOWN_LOAD_TIMEOUT:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.TASK_DOWNLOAD_FAIL:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.Parts_System_Not_Allow_Point_Regist:
                    break;
                case TASK_DOWNLOAD_RETURN_CODES.Homing_Trajectory_Error:
                    break;
                default:
                    break;
            }
        }

        public override ALARMS Alarm_Code { get; set; } = ALARMS.Download_Task_To_AGV_Fail;
    }

    [Serializable]
    internal class RegionNotEnterableException : VMSExceptionAbstract
    {
        private TASK_DOWNLOAD_RETURN_CODES returnCode;
        public override ALARMS Alarm_Code { get; set; } = ALARMS.REGION_NOT_ENTERABLE;
    }
}
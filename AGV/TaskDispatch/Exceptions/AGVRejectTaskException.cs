using AGVSystemCommonNet6.AGVDispatch.Messages;
using System.Runtime.Serialization;

namespace VMSystem.AGV.TaskDispatch.Exceptions
{
    [Serializable]
    internal class AGVRejectTaskException : Exception
    {
        private TASK_DOWNLOAD_RETURN_CODES returnCode;

        public AGVRejectTaskException()
        {
        }

        public AGVRejectTaskException(TASK_DOWNLOAD_RETURN_CODES returnCode)
        {
            this.returnCode = returnCode;
        }

        public AGVRejectTaskException(string? message) : base(message)
        {
        }

        public AGVRejectTaskException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected AGVRejectTaskException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
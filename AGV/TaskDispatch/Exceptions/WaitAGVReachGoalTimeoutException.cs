using System.Runtime.Serialization;

namespace VMSystem.AGV.TaskDispatch.Exceptions
{
    [Serializable]
    internal class WaitAGVReachGoalTimeoutException : Exception
    {
        public WaitAGVReachGoalTimeoutException()
        {
        }

        public WaitAGVReachGoalTimeoutException(string? message) : base(message)
        {
        }

        public WaitAGVReachGoalTimeoutException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected WaitAGVReachGoalTimeoutException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
using System.Runtime.Serialization;

namespace VMSystem.AGV.TaskDispatch.Exceptions
{
    [Serializable]
    internal class WaitAGVReachGoalCanceledException : Exception
    {
        public WaitAGVReachGoalCanceledException()
        {
        }

        public WaitAGVReachGoalCanceledException(string? message) : base(message)
        {
        }

        public WaitAGVReachGoalCanceledException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected WaitAGVReachGoalCanceledException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
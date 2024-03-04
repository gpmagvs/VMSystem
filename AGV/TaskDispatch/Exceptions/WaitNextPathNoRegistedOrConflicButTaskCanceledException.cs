using System.Runtime.Serialization;

namespace VMSystem.AGV.TaskDispatch.Exceptions
{
    [Serializable]
    internal class WaitNextPathNoRegistedOrConflicButTaskCanceledException : Exception
    {
        public WaitNextPathNoRegistedOrConflicButTaskCanceledException()
        {
        }

        public WaitNextPathNoRegistedOrConflicButTaskCanceledException(string? message) : base(message)
        {
        }

        public WaitNextPathNoRegistedOrConflicButTaskCanceledException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected WaitNextPathNoRegistedOrConflicButTaskCanceledException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
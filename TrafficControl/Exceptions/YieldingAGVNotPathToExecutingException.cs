using System.Runtime.Serialization;

namespace VMSystem.TrafficControl.Exceptions
{
    [Serializable]
    internal class YieldingAGVNotPathToExecutingException : Exception
    {
        public YieldingAGVNotPathToExecutingException()
        {
        }

        public YieldingAGVNotPathToExecutingException(string? message) : base(message)
        {
        }

        public YieldingAGVNotPathToExecutingException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected YieldingAGVNotPathToExecutingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
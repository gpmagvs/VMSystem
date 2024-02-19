using System.Runtime.Serialization;

namespace VMSystem.AGV.TaskDispatch.Exceptions
{
    [Serializable]
    internal class NotFoundAGVException : Exception
    {
        public NotFoundAGVException()
        {
        }

        public NotFoundAGVException(string? message) : base(message)
        {
        }

        public NotFoundAGVException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

        protected NotFoundAGVException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
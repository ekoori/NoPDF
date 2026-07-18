using System;

namespace DjvuNet.Errors
{
    public class DjvuInvalidOperationException : InvalidOperationException
    {
        public DjvuInvalidOperationException() : base()
        {
        }

        public DjvuInvalidOperationException(string message) : base(message)
        {
        }

        public DjvuInvalidOperationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
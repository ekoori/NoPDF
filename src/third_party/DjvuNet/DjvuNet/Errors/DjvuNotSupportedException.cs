using System;

namespace DjvuNet.Errors
{
    public class DjvuNotSupportedException : NotSupportedException
    {
        public DjvuNotSupportedException() : base()
        {
        }

        public DjvuNotSupportedException(string message) : base(message)
        {
        }

        public DjvuNotSupportedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
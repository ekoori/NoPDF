using System;

namespace DjvuNet.Errors
{
    public class DjvuArgumentOutOfRangeException : ArgumentOutOfRangeException
    {
        public DjvuArgumentOutOfRangeException() : base()
        {
        }

        public DjvuArgumentOutOfRangeException(string paramName) : base(paramName)
        {
        }

        public DjvuArgumentOutOfRangeException(string message, Exception innerException) : base (message, innerException)
        {
        }

        public DjvuArgumentOutOfRangeException(string paramName, string message) : base (paramName, message)
        {
        }

        public DjvuArgumentOutOfRangeException(string paramName, object actualValue, string message)
            : base(paramName, actualValue, message)
        {
        }
    }
}
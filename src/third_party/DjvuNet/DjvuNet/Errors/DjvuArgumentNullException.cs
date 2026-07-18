using System;

namespace DjvuNet.Errors
{
    public class DjvuArgumentNullException  : ArgumentNullException
    {
        public DjvuArgumentNullException() : base()
        {
        }

        public DjvuArgumentNullException(string paramName) : base(paramName)
        {
        }

        public DjvuArgumentNullException(string message, Exception innerException) : base (message, innerException)
        {
        }

        public DjvuArgumentNullException(string paramName, string message) : base (paramName, message)
        {
        }
    }
}
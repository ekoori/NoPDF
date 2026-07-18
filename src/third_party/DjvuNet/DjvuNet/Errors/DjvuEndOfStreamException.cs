using System;
using System.IO;

namespace DjvuNet.Errors
{
    public class DjvuEndOfStreamException : EndOfStreamException
    {
        public DjvuEndOfStreamException() : base()
        {
        }

        public DjvuEndOfStreamException(string message) : base(message)
        {
        }

        public DjvuEndOfStreamException(string message, Exception innerException)
            : base (message, innerException)
        {
        }
    }
}
using System;
using System.IO;

namespace DjvuNet.Errors
{
    public class DjvuFileNotFoundException : FileNotFoundException
    {
        public DjvuFileNotFoundException() : base()
        {
        }

        public DjvuFileNotFoundException(string message) : base(message)
        {
        }

        public DjvuFileNotFoundException(string message, Exception innerException) : base (message, innerException)
        {
        }

        public DjvuFileNotFoundException(string message, string fileName) : base (message, fileName)
        {
        }

        public DjvuFileNotFoundException(string message, string fileName, Exception innerException)
            : base(message, fileName, innerException)
        {
        }
    }
}
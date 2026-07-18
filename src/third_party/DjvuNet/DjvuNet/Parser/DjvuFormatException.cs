using System;

namespace DjvuNet
{
    public class DjvuFormatException : System.FormatException
    {
        //
        // Summary:
        //     Initializes a new instance of the DjvuNet.DjvuFormatException class.
        public DjvuFormatException()
        {
        }
        //
        // Summary:
        //     Initializes a new instance of the DjvuNet.IFFParsingException class with a specified
        //     error message.
        //
        // Parameters:
        //   message:
        //     The message that describes the error.
        public DjvuFormatException(string message) : base(message)
        {
        }
        //
        // Summary:
        //     Initializes a new instance of the DjvuNet.IFFParsingException class with a specified
        //     error message and a reference to the inner exception that is the cause of this
        //     exception.
        //
        // Parameters:
        //   message:
        //     The error message that explains the reason for the exception.
        //
        //   innerException:
        //     The exception that is the cause of the current exception. If the innerException
        //     parameter is not a null reference (Nothing in Visual Basic), the current exception
        //     is raised in a catch block that handles the inner exception.
        public DjvuFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace NoPdf.App;

/// <summary>
/// Ensures a single running window: a second launch forwards its file arguments
/// to the primary instance (which opens them as new tabs) and then exits.
/// </summary>
public static class SingleInstance
{
    private static readonly string PipeName =
        "NoPdf.SingleInstance." + Environment.UserName;

    /// <summary>
    /// If a primary instance is already running, sends it the given paths and
    /// returns true (the caller should exit). Otherwise returns false.
    /// </summary>
    public static bool TryForward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            foreach (var a in args) writer.WriteLine(a);
            return true;
        }
        catch
        {
            return false; // no primary instance listening
        }
    }

    /// <summary>Starts listening for forwarded paths from secondary launches.</summary>
    public static void StartServer(Action<string[]> onPaths)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var lines = reader.ReadToEnd()
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.TrimEnd('\r'))
                        .ToArray();
                    onPaths(lines);
                }
                catch
                {
                    Thread.Sleep(200); // transient error; keep listening
                }
            }
        })
        { IsBackground = true, Name = "NoPdf-SingleInstance" };
        thread.Start();
    }
}

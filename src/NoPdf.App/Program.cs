using Avalonia;
using System;

namespace NoPdf.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Dump the documented default config without starting the UI. The default is
        // generated (the command list is rendered into it), so shipping a copy alongside
        // a release means asking the app for it. Used by scripts/release.ps1.
        if (args.Length >= 2 && args[0] == "--write-default-config")
        {
            try
            {
                System.IO.File.WriteAllText(args[1], Config.AppConfig.DefaultYaml);
                Console.WriteLine($"Wrote default config to {args[1]}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not write config: {ex.Message}");
                Environment.ExitCode = 1;
            }
            return;
        }

        // If another window is already open, hand our file args to it and exit.
        if (SingleInstance.TryForward(args)) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

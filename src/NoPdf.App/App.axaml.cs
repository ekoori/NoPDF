using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NoPdf.App.ViewModels;

namespace NoPdf.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // Apply the configured theme (light | dark | inherit).
            if (window.DataContext is MainWindowViewModel mvm)
            {
                RequestedThemeVariant = mvm.Config.Theme.ToLowerInvariant() switch
                {
                    "light" => Avalonia.Styling.ThemeVariant.Light,
                    "dark" => Avalonia.Styling.ThemeVariant.Dark,
                    _ => Avalonia.Styling.ThemeVariant.Default, // inherit from OS
                };
            }

            // Open any .pdf paths passed on the command line ("Open with…"),
            // otherwise restore the previous session's tabs.
            var args = desktop.Args ?? System.Array.Empty<string>();
            var pdfs = args.Where(a => File.Exists(a) &&
                                       a.EndsWith(".pdf", System.StringComparison.OrdinalIgnoreCase))
                           .ToArray();
            if (window.DataContext is MainWindowViewModel vm)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    if (pdfs.Length > 0)
                        foreach (var p in pdfs) await vm.OpenPathAsync(p);
                    else
                        await vm.RestoreSessionAsync();
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

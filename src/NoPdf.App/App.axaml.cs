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
                                       NoPdf.Core.Import.DocumentImport.IsSupportedDocument(a))
                           .ToArray();
            if (window.DataContext is MainWindowViewModel vm)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    // Always restore the last session, then add any file passed on launch.
                    await vm.RestoreSessionAsync();
                    // Focus the tab if the restored session already has this file.
                    foreach (var p in pdfs) await vm.OpenPathAsync(p);
                });

                // Secondary launches forward their files here.
                SingleInstance.StartServer(paths => Dispatcher.UIThread.Post(async () =>
                {
                    window.Show();
                    window.Activate();
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                        window.WindowState = Avalonia.Controls.WindowState.Normal;
                    foreach (var p in paths)
                        if (File.Exists(p) && NoPdf.Core.Import.DocumentImport.IsSupportedDocument(p))
                            await vm.OpenPathAsync(p); // focus it if it's already open
                }));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

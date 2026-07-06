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

            // Open any .pdf paths passed on the command line ("Open with…").
            var args = desktop.Args ?? System.Array.Empty<string>();
            var pdfs = args.Where(a => File.Exists(a) &&
                                       a.EndsWith(".pdf", System.StringComparison.OrdinalIgnoreCase))
                           .ToArray();
            if (pdfs.Length > 0 && window.DataContext is MainWindowViewModel vm)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    foreach (var p in pdfs)
                        await vm.OpenPathAsync(p);
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

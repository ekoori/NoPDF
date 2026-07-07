using System;
using System.IO;
using System.Threading;
using Avalonia.Threading;

namespace NoPdf.App.Config;

/// <summary>Watches the config file and invokes a callback (debounced, on the UI thread) when it changes.</summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action _onChanged;
    private readonly Timer _debounce;

    public ConfigWatcher(string path, Action onChanged)
    {
        _onChanged = onChanged;
        _debounce = new Timer(_ => Dispatcher.UIThread.Post(_onChanged), null, Timeout.Infinite, Timeout.Infinite);

        var dir = Path.GetDirectoryName(path)!;
        var file = Path.GetFileName(path);
        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
        => _debounce.Change(200, Timeout.Infinite); // coalesce rapid save events

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}

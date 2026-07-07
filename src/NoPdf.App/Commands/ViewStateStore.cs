using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>Remembers the zoom and scroll position per file, keyed by path.</summary>
public sealed class ViewStateStore
{
    public sealed class Entry
    {
        public double Zoom { get; set; } = 1.0;
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
    }

    private readonly string _path;
    private Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);

    public ViewStateStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "viewstate.json");
        Load();
    }

    public Entry? Get(string file) => _map.TryGetValue(file, out var e) ? e : null;

    public void Set(string file, double zoom, double ox, double oy)
    {
        _map[file] = new Entry { Zoom = zoom, OffsetX = ox, OffsetY = oy };
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
            if (data is not null) _map = new(data, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map)); }
        catch { }
    }
}

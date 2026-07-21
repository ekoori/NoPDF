using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>Remembers the zoom, scroll position and view mode per file, keyed by path.</summary>
public sealed class ViewStateStore
{
    public sealed class Entry
    {
        public double Zoom { get; set; } = 1.0;
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        /// <summary>`:view` mode name ("scroll", "full", "scrollh"); blank = never set.</summary>
        public string Mode { get; set; } = "";
        /// <summary>Pages across (scroll/full) or rows down (scrollh).</summary>
        public int Pages { get; set; } = 1;

        /// <summary>A label the user gave the tab. Blank = show the file name. Never affects
        /// the file itself; it only changes what the tab strip reads.</summary>
        public string Name { get; set; } = "";
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

    /// <summary>Records the position, leaving the file's view mode alone.</summary>
    public void Set(string file, double zoom, double ox, double oy)
    {
        var e = Get(file) ?? new Entry();
        e.Zoom = zoom; e.OffsetX = ox; e.OffsetY = oy;
        _map[file] = e;
        Save();
    }

    /// <summary>Records a custom tab label (blank clears it), leaving everything else alone.</summary>
    public void SetName(string file, string? name)
    {
        var e = Get(file) ?? new Entry();
        e.Name = name ?? "";
        _map[file] = e;
        Save();
    }

    /// <summary>Records the view mode, leaving the file's position alone.</summary>
    public void SetMode(string file, string mode, int pages)
    {
        var e = Get(file) ?? new Entry();
        e.Mode = mode; e.Pages = pages;
        _map[file] = e;
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

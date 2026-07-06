using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>Most-recently-opened file paths, persisted under application data.</summary>
public sealed class RecentFiles
{
    private const int MaxEntries = 12;
    private readonly string _path;
    private List<string> _files = new();

    public RecentFiles()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "recent.json");
        Load();
    }

    public IReadOnlyList<string> Files => _files;

    public void Add(string path)
    {
        _files.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _files.Insert(0, path);
        if (_files.Count > MaxEntries) _files.RemoveRange(MaxEntries, _files.Count - MaxEntries);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
            if (data is not null) _files = data.Where(File.Exists).ToList();
        }
        catch { }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_files)); }
        catch { }
    }
}

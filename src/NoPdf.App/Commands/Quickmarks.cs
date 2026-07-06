using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>
/// Named shortcuts to PDF files (qutebrowser-style quickmarks), persisted as JSON
/// under the user's application-data folder.
/// </summary>
public sealed class Quickmarks
{
    private readonly string _path;
    private Dictionary<string, string> _marks = new(StringComparer.OrdinalIgnoreCase);

    public Quickmarks()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "quickmarks.json");
        Load();
    }

    public IReadOnlyDictionary<string, string> All => _marks;

    public string? Resolve(string name)
        => _marks.TryGetValue(name, out var p) ? p : null;

    public void Set(string name, string path)
    {
        _marks[name] = path;
        Save();
    }

    public bool Remove(string name)
    {
        bool ok = _marks.Remove(name);
        if (ok) Save();
        return ok;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data is not null)
                _marks = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* start empty on any corruption */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_marks, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}

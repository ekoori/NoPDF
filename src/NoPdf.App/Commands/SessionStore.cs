using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>Persists which files are open, their tab order, and the active tab.</summary>
public sealed class SessionStore
{
    public sealed class SessionData
    {
        public List<string> Files { get; set; } = new();
        public int Active { get; set; } = -1;
    }

    private readonly string _path;
    private readonly string _namedPath;

    public SessionStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "session.json");
        _namedPath = Path.Combine(dir, "sessions.json");
    }

    public SessionData? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(_path));
        }
        catch { return null; }
    }

    public void Save(SessionData data)
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ----- Named sessions -----

    private Dictionary<string, SessionData> LoadNamed()
    {
        try
        {
            if (File.Exists(_namedPath))
                return JsonSerializer.Deserialize<Dictionary<string, SessionData>>(File.ReadAllText(_namedPath))
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveNamedAll(Dictionary<string, SessionData> all)
    {
        try
        {
            File.WriteAllText(_namedPath,
                JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void SaveNamed(string name, SessionData data)
    {
        var all = LoadNamed();
        all[name] = data;
        SaveNamedAll(all);
    }

    public SessionData? GetNamed(string name)
        => LoadNamed().TryGetValue(name, out var d) ? d : null;

    public bool DeleteNamed(string name)
    {
        var all = LoadNamed();
        if (!all.Remove(name)) return false;
        SaveNamedAll(all);
        return true;
    }

    public IReadOnlyCollection<string> NamedSessions() => LoadNamed().Keys;
}

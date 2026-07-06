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

    public SessionStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "session.json");
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
}

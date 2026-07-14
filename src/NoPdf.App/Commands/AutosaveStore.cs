using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>
/// Caches unsaved edits to a temp folder so they survive a crash or a forced exit.
/// Each entry keeps the ORIGINAL file path, so recovered content still saves back to
/// the real document.
/// </summary>
public sealed class AutosaveStore
{
    public sealed class Entry
    {
        public string OriginalPath { get; set; } = "";
        public string CacheFile { get; set; } = "";
        public DateTime SavedUtc { get; set; }
    }

    private readonly string _dir;
    private readonly string _indexPath;
    private Dictionary<string, Entry> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a cached copy is kept before it's discarded.</summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(24);

    public AutosaveStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf", "autosave");
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_indexPath)) ?? new();
            _index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list)
                if (File.Exists(Path.Combine(_dir, e.CacheFile))) _index[e.OriginalPath] = e;
        }
        catch { _index = new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Drops cached copies older than <see cref="Expiry"/> (and orphaned files).</summary>
    public void PruneExpired()
    {
        try
        {
            var cutoff = DateTime.UtcNow - Expiry;
            foreach (var key in new List<string>(_index.Keys))
                if (_index[key].SavedUtc < cutoff) Remove(key);

            // Sweep cache files no index entry refers to any more.
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _index.Values) known.Add(e.CacheFile);
            foreach (var f in Directory.GetFiles(_dir, "*.pdf"))
                if (!known.Contains(Path.GetFileName(f)))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private void Flush()
    {
        try
        {
            var list = new List<Entry>(_index.Values);
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string KeyFor(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8) + ".pdf";
    }

    /// <summary>Writes the document's current bytes to the cache.</summary>
    public void Save(string originalPath, byte[] bytes)
    {
        try
        {
            string name = KeyFor(originalPath);
            File.WriteAllBytes(Path.Combine(_dir, name), bytes);
            _index[originalPath] = new Entry
            {
                OriginalPath = originalPath, CacheFile = name, SavedUtc = DateTime.UtcNow,
            };
            Flush();
        }
        catch { }
    }

    /// <summary>Cached unsaved bytes for a file, or null when there are none.</summary>
    public byte[]? TryLoad(string originalPath)
    {
        try
        {
            if (!_index.TryGetValue(originalPath, out var e)) return null;
            var p = Path.Combine(_dir, e.CacheFile);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    /// <summary>Drops the cache for a file (after it's been saved, or discarded).</summary>
    public void Remove(string originalPath)
    {
        try
        {
            if (!_index.Remove(originalPath, out var e)) return;
            var p = Path.Combine(_dir, e.CacheFile);
            if (File.Exists(p)) File.Delete(p);
            Flush();
        }
        catch { }
    }
}

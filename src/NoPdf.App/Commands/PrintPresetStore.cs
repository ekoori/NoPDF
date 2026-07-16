using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NoPdf.App.Printing;

namespace NoPdf.App.Commands;

/// <summary>A named set of print options, usable as <c>:print &lt;name&gt;</c>.</summary>
public sealed class PrintPreset
{
    public string Name { get; set; } = "default";
    public string Printer { get; set; } = "";
    public int Copies { get; set; } = 1;
    public bool FitToPage { get; set; } = true;
    public bool Grayscale { get; set; }
    public bool Landscape { get; set; }
    /// <summary>The preset a bare <c>:print</c> uses. Only one is marked at a time.</summary>
    public bool IsDefault { get; set; }

    public PrintOptions ToOptions() => new()
    {
        Printer = Printer,
        Copies = Copies,
        FitToPage = FitToPage,
        Grayscale = Grayscale,
        Landscape = Landscape,
    };

    public static PrintPreset From(string name, PrintOptions o, bool isDefault) => new()
    {
        Name = name,
        Printer = o.Printer,
        Copies = o.Copies,
        FitToPage = o.FitToPage,
        Grayscale = o.Grayscale,
        Landscape = o.Landscape,
        IsDefault = isDefault,
    };
}

/// <summary>Persists named print presets to %AppData%/NoPdf/print_presets.json.</summary>
public sealed class PrintPresetStore
{
    private readonly string _path;
    private List<PrintPreset> _presets = new();

    public PrintPresetStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "print_presets.json");
        Load();
    }

    public IReadOnlyList<PrintPreset> Presets => _presets;

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _presets = JsonSerializer.Deserialize<List<PrintPreset>>(File.ReadAllText(_path)) ?? new();
        }
        catch { _presets = new(); }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public PrintPreset? Find(string name)
        => _presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset a bare <c>:print</c> uses, or null when none is marked.</summary>
    public PrintPreset? Default => _presets.FirstOrDefault(p => p.IsDefault);

    /// <summary>Adds or replaces a preset by name. Marking it default clears the others.</summary>
    public void Save(PrintPreset preset)
    {
        _presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (preset.IsDefault)
            foreach (var p in _presets) p.IsDefault = false;
        _presets.Add(preset);
        Save();
    }

    public bool Remove(string name)
    {
        int n = _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (n > 0) Save();
        return n > 0;
    }

    /// <summary>Makes an existing preset the one a bare <c>:print</c> uses.</summary>
    public bool MakeDefault(string name)
    {
        var target = Find(name);
        if (target is null) return false;
        foreach (var p in _presets) p.IsDefault = false;
        target.IsDefault = true;
        Save();
        return true;
    }
}

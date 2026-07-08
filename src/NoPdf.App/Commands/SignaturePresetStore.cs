using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NoPdf.App.Commands;

/// <summary>Persists reusable signature presets.</summary>
public sealed class SignaturePresetStore
{
    public sealed class Dto
    {
        public string Name { get; set; } = "Signature";
        public string Alias { get; set; } = "";
        public string FrameColor { get; set; } = "#1E6EDC";
        public double FrameThickness { get; set; } = 1.5;
        public double FrameOpacity { get; set; } = 1.0;
        public bool UseCertificate { get; set; }
        public string CertPath { get; set; } = "";
        public string CertPassword { get; set; } = "";
    }

    private readonly string _path;

    public SignaturePresetStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoPdf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "sig_presets.json");
    }

    public List<Dto> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<Dto>>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save(IEnumerable<Dto> presets)
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

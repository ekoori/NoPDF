using CommunityToolkit.Mvvm.ComponentModel;

namespace NoPdf.App.ViewModels;

/// <summary>A reusable signature style: name, alias, frame appearance and optional certificate.</summary>
public sealed partial class SignaturePreset : ViewModelBase
{
    [ObservableProperty] private string _name = "Signature";
    /// <summary>Short handle used with <c>:sign &lt;alias&gt;</c>.</summary>
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _frameColor = "#1E6EDC";
    [ObservableProperty] private double _frameThickness = 1.5;
    [ObservableProperty] private double _frameOpacity = 1.0;

    /// <summary>When set, placing this signature also applies a cryptographic certificate.</summary>
    [ObservableProperty] private bool _useCertificate;
    [ObservableProperty] private string _certPath = "";
    [ObservableProperty] private string _certPassword = "";
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace NoPdf.App.ViewModels;

/// <summary>A reusable signature style (name + frame appearance).</summary>
public sealed partial class SignaturePreset : ViewModelBase
{
    [ObservableProperty] private string _name = "Signature";
    [ObservableProperty] private string _frameColor = "#1E6EDC";
    [ObservableProperty] private double _frameThickness = 1.5;
    [ObservableProperty] private double _frameOpacity = 1.0;
}

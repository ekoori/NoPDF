using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoPdf.Core.Annotations;

namespace NoPdf.App.ViewModels;

/// <summary>
/// Editable wrapper over the currently-selected annotation, shown in the
/// properties panel. Property changes mutate the model, redraw the overlay,
/// mark the document dirty and (once per selection) push an undo snapshot.
/// </summary>
public sealed partial class AnnotationEditorViewModel : ViewModelBase
{
    private readonly DocumentViewModel _owner;
    private readonly PageViewModel _page;
    private readonly PdfAnnotationModel _ann;
    private bool _changed;
    private bool _loading;

    public string TypeName { get; }
    public bool HasStroke { get; }
    public bool HasWidth { get; }
    public bool HasFill { get; }
    public bool HasFont { get; }
    public bool HasTextColor { get; }
    public bool HasOpacity { get; }
    public bool HasContents { get; }

    [ObservableProperty] private string _strokeHex = "#000000";
    [ObservableProperty] private string _textHex = "#000000";
    [ObservableProperty] private bool _fillEnabled;
    [ObservableProperty] private string _fillHex = "#FFF3A0";
    [ObservableProperty] private double _strokeWidth = 2;
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private double _opacity = 1;
    [ObservableProperty] private string _contents = "";

    public IReadOnlyList<string> Palette { get; } = new[]
    {
        "#E53935", "#FB8C00", "#FDD835", "#43A047", "#1E88E5",
        "#8E24AA", "#00897B", "#000000", "#FFFFFF", "#9E9E9E",
    };

    public AnnotationEditorViewModel(DocumentViewModel owner, PageViewModel page, PdfAnnotationModel ann)
    {
        _owner = owner;
        _page = page;
        _ann = ann;
        _loading = true;

        TypeName = ann switch
        {
            HighlightAnnotation => "Highlight",
            SquareAnnotation => "Rectangle",
            LineAnnotation { Arrow: true } => "Arrow",
            LineAnnotation => "Line",
            PolylineAnnotation => "Polyline",
            CalloutAnnotation => "Callout",
            FreeTextAnnotation => "Text box",
            StickyNoteAnnotation => "Sticky note",
            _ => "Annotation",
        };

        StrokeHex = Hex(ann.Color);
        StrokeWidth = ann.StrokeWidth;
        Contents = ann.Contents ?? "";

        switch (ann)
        {
            case HighlightAnnotation:
                HasStroke = true; HasContents = true; break;
            case SquareAnnotation s:
                HasStroke = HasWidth = HasFill = HasContents = true;
                FillEnabled = s.Interior is not null;
                if (s.Interior is { } ic) FillHex = Hex(ic);
                break;
            case FreeTextAnnotation f: // also CalloutAnnotation
                HasStroke = HasWidth = HasFont = HasTextColor = HasOpacity = HasContents = true;
                FontSize = f.FontSize; Opacity = f.BorderOpacity; TextHex = Hex(f.TextColor);
                break;
            case LineAnnotation or PolylineAnnotation:
                HasStroke = HasWidth = HasContents = true; break;
            case StickyNoteAnnotation:
                HasStroke = true; HasContents = true; break;
        }
        _loading = false;
    }

    [RelayCommand] private void PickStroke(string hex) => StrokeHex = hex;
    [RelayCommand] private void PickText(string hex) => TextHex = hex;
    [RelayCommand] private void PickFill(string hex) { FillEnabled = true; FillHex = hex; }

    partial void OnStrokeHexChanged(string value) => Apply(() => _ann.Color = Parse(value, _ann.Color));
    partial void OnStrokeWidthChanged(double value) => Apply(() => _ann.StrokeWidth = value);
    partial void OnContentsChanged(string value) => Apply(() => _ann.Contents = value);

    partial void OnFontSizeChanged(double value)
        => Apply(() => { if (_ann is FreeTextAnnotation f) f.FontSize = value; });
    partial void OnOpacityChanged(double value)
        => Apply(() => { if (_ann is FreeTextAnnotation f) f.BorderOpacity = value; });
    partial void OnTextHexChanged(string value)
        => Apply(() => { if (_ann is FreeTextAnnotation f) f.TextColor = Parse(value, f.TextColor); });
    partial void OnFillEnabledChanged(bool value) => ApplyFill();
    partial void OnFillHexChanged(string value) => ApplyFill();

    private void ApplyFill()
        => Apply(() => { if (_ann is SquareAnnotation s) s.Interior = FillEnabled ? Parse(FillHex, AnnotColor.Yellow) : null; });

    private void Apply(Action mutate)
    {
        if (_loading) return;
        if (!_changed) { _owner.BeginChange(); _changed = true; }
        mutate();
        _owner.MarkDirty();
        _page.NotifyAnnotationChanged();
    }

    private static string Hex(AnnotColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static AnnotColor Parse(string hex, AnnotColor fallback)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return new AnnotColor(r, g, b);
        return fallback;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoPdf.Core.Annotations;

namespace NoPdf.App.ViewModels;

/// <summary>
/// Editable wrapper over the current annotation selection, shown in the properties
/// panel. Property changes mutate every selected annotation that supports them,
/// redraw the overlay, mark the document dirty and (once per edit gesture) push an
/// undo snapshot. Displayed values come from the primary (last-selected) annotation.
/// </summary>
public sealed partial class AnnotationEditorViewModel : ViewModelBase
{
    private readonly DocumentViewModel _owner;
    private readonly IReadOnlyList<(PageViewModel page, PdfAnnotationModel ann)> _targets;
    private readonly PdfAnnotationModel _primary;
    private bool _changed;
    private bool _loading;

    public int Count => _targets.Count;
    public bool MultiSelect => _targets.Count > 1;
    public string TypeName { get; }
    public bool HasStroke { get; }
    public bool HasWidth { get; }
    public bool HasFill { get; }
    public bool HasFont { get; }
    public bool HasTextColor { get; }
    public bool HasOpacity { get; }
    public bool HasContents { get; }
    public bool HasSigner { get; }
    public bool HasFrameToggle { get; }
    public string ContentsLabel { get; private set; } = "Note / text";

    [ObservableProperty] private string _strokeHex = "#000000";
    [ObservableProperty] private string _textHex = "#000000";
    [ObservableProperty] private bool _fillEnabled;
    [ObservableProperty] private string _fillHex = "#FFF3A0";
    [ObservableProperty] private double _strokeWidth = 2;
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private double _opacity = 1;
    [ObservableProperty] private string _contents = "";
    [ObservableProperty] private string _signerName = "";
    [ObservableProperty] private bool _frameEnabled;

    public IReadOnlyList<string> Palette { get; } = new[]
    {
        "#E53935", "#FB8C00", "#FDD835", "#43A047", "#1E88E5",
        "#8E24AA", "#00897B", "#000000", "#FFFFFF", "#9E9E9E",
    };

    public AnnotationEditorViewModel(
        DocumentViewModel owner, IReadOnlyList<(PageViewModel page, PdfAnnotationModel ann)> targets)
    {
        _owner = owner;
        _targets = targets;
        _primary = targets[^1].ann;
        _loading = true;

        var ann = _primary;
        TypeName = MultiSelect
            ? $"{targets.Count} annotations"
            : ann switch
            {
                HighlightAnnotation => "Highlight",
                SquareAnnotation => "Rectangle",
                ImageAnnotation => "Image",
                LineAnnotation { Arrow: true } => "Arrow",
                LineAnnotation => "Line",
                PolylineAnnotation => "Polyline",
                SignatureAnnotation => "Signature",
                CalloutAnnotation => "Callout",
                FreeTextAnnotation => "Text box",
                StickyNoteAnnotation => "Sticky note",
                _ => "Annotation",
            };

        StrokeHex = Hex(ann.Color);
        StrokeWidth = ann.StrokeWidth;
        Contents = ann.Contents ?? "";

        // Capability flags: union across the selection so a shared property is
        // editable whenever any selected annotation supports it.
        foreach (var (_, a) in targets)
        {
            switch (a)
            {
                case HighlightAnnotation:
                    HasStroke = HasContents = true; break;
                case SquareAnnotation s:
                    HasStroke = HasWidth = HasFill = HasContents = true;
                    if (ReferenceEquals(a, ann)) { FillEnabled = s.Interior is not null; if (s.Interior is { } ic) FillHex = Hex(ic); }
                    break;
                case ImageAnnotation im:
                    HasWidth = HasOpacity = HasStroke = HasFrameToggle = true;
                    if (ReferenceEquals(a, ann)) { Opacity = im.Opacity; FrameEnabled = im.Border; }
                    break;
                case SignatureAnnotation sg:
                    HasStroke = HasWidth = HasOpacity = HasSigner = HasContents = true;
                    ContentsLabel = "Note (optional)";
                    if (ReferenceEquals(a, ann)) { SignerName = sg.SignerName; Opacity = sg.BorderOpacity; }
                    break;
                case FreeTextAnnotation f: // also CalloutAnnotation
                    HasStroke = HasWidth = HasFont = HasTextColor = HasOpacity = HasContents = true;
                    if (ReferenceEquals(a, ann)) { FontSize = f.FontSize; Opacity = f.BorderOpacity; TextHex = Hex(f.TextColor); }
                    break;
                case LineAnnotation or PolylineAnnotation:
                    HasStroke = HasWidth = HasContents = true; break;
                case StickyNoteAnnotation:
                    HasStroke = HasContents = true; break;
            }
        }
        // Contents only makes sense to edit for a single annotation.
        if (MultiSelect) HasContents = HasSigner = false;
        _loading = false;
    }

    [RelayCommand] private void PickStroke(string hex) => StrokeHex = hex;
    [RelayCommand] private void PickText(string hex) => TextHex = hex;
    [RelayCommand] private void PickFill(string hex) { FillEnabled = true; FillHex = hex; }

    partial void OnSignerNameChanged(string value)
        => Apply(a => { if (a is SignatureAnnotation s) s.SignerName = value; });

    partial void OnStrokeHexChanged(string value) => Apply(a => a.Color = Parse(value, a.Color));
    partial void OnStrokeWidthChanged(double value) => Apply(a => a.StrokeWidth = value);
    partial void OnContentsChanged(string value) => Apply(a => a.Contents = value);

    partial void OnFontSizeChanged(double value)
        => Apply(a => { if (a is FreeTextAnnotation f) f.FontSize = value; });
    partial void OnOpacityChanged(double value)
        => Apply(a =>
        {
            if (a is ImageAnnotation im) im.Opacity = value;
            else if (a is FreeTextAnnotation f) f.BorderOpacity = value;
        });
    partial void OnTextHexChanged(string value)
        => Apply(a => { if (a is FreeTextAnnotation f) f.TextColor = Parse(value, f.TextColor); });
    partial void OnFillEnabledChanged(bool value) => ApplyFill();
    partial void OnFillHexChanged(string value) => ApplyFill();
    partial void OnFrameEnabledChanged(bool value)
        => Apply(a => { if (a is ImageAnnotation im) im.Border = value; });

    private void ApplyFill()
        => Apply(a => { if (a is SquareAnnotation s) s.Interior = FillEnabled ? Parse(FillHex, AnnotColor.Yellow) : null; });

    private void Apply(Action<PdfAnnotationModel> mutate)
    {
        if (_loading) return;
        if (!_changed) { _owner.BeginChange(); _changed = true; }
        foreach (var (page, ann) in _targets)
        {
            mutate(ann);
            page.NotifyAnnotationChanged();
        }
        _owner.MarkDirty();
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

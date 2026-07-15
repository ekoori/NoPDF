using Avalonia.Media;
using NoPdf.Core.Signing;

namespace NoPdf.App.ViewModels;

/// <summary>A row in the "in this document" section of the signatures panel: one
/// embedded digital signature and the verdict on it.</summary>
public sealed class DocumentSignatureItem
{
    public SignatureInfo Info { get; }

    public DocumentSignatureItem(SignatureInfo info) => Info = info;

    public string Signer => Info.Signer;

    public string When => Info.SignedUtc is { } t
        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "no timestamp";

    public string Summary => Info.Summary;

    /// <summary>Green only when nothing is wrong; red when the content changed under the
    /// signature; amber for the middle ground (intact but untrusted or superseded).</summary>
    public IBrush StatusColor => Info.Error is not null || !Info.IntegrityOk
        ? Brushes.Red
        : Info.IsFullyValid ? Green : Amber;

    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00));

    public string Badge => Info.Error is not null || !Info.IntegrityOk
        ? "✕" : Info.IsFullyValid ? "✓" : "!";
}

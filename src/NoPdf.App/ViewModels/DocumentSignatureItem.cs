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

    /// <summary>Red only when we know the content changed (or the CMS is unreadable);
    /// green when nothing is wrong; amber for the middle ground — untrusted, appended to,
    /// or a malformed byte range we couldn't check.</summary>
    public IBrush StatusColor => IsBroken ? Brushes.Red
        : Info.IsFullyValid ? Green : Amber;

    public string Badge => IsBroken ? "✕" : Info.IsFullyValid ? "✓" : "!";

    /// <summary>Definitely bad: unreadable, or integrity was checked and failed.</summary>
    private bool IsBroken => Info.Error is not null || (Info.IntegrityChecked && !Info.IntegrityOk);

    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00));
}

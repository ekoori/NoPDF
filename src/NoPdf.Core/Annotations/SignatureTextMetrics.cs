using System;

namespace NoPdf.Core.Annotations;

/// <summary>
/// Type sizes for a signature stamp, scaled to the frame the user drew: a bigger stamp
/// gets bigger writing rather than the same small text in a large box.
///
/// Shared by the on-screen overlay and the appearance stream written into the PDF, so the
/// preview and the saved file agree.
/// </summary>
public readonly record struct SignatureTextMetrics(
    double NameSize, double ReasonSize, double DateSize,
    double ReasonLead, double DateLead, double Pad)
{
    // The layout these proportions are taken from.
    private const double RefHeight = 90;
    private const double RefName = 12, RefReason = 10, RefDate = 8;
    private const double RefReasonLead = 16, RefDateLead = 14, RefPad = 6;

    public static SignatureTextMetrics For(double frameWidth, double frameHeight,
        string? signerName, string? reason)
    {
        double k = frameHeight / RefHeight;

        // Don't let the longest line run outside the frame. Helvetica averages ~0.5em per
        // character, which is close enough to keep the text inside its box.
        int longest = Math.Max(signerName?.Length ?? 0, (reason?.Length ?? 0) * 10 / 12);
        if (longest > 0)
        {
            double maxByWidth = (frameWidth - 2 * RefPad * k) / (longest * 0.5);
            if (maxByWidth > 0) k = Math.Min(k, maxByWidth / RefName);
        }
        k = Math.Clamp(k, 0.45, 6.0);

        return new SignatureTextMetrics(
            RefName * k, RefReason * k, RefDate * k,
            RefReasonLead * k, RefDateLead * k, RefPad * k);
    }
}

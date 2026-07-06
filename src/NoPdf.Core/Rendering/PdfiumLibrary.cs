using PDFiumCore;

namespace NoPdf.Core.Rendering;

/// <summary>
/// Owns one-time initialization of the native PDFium library and a global lock.
/// PDFium is NOT thread-safe: every call into any fpdf* API must be made while
/// holding <see cref="Sync"/>. Render work should run off the UI thread, but the
/// lock guarantees calls are serialized.
/// </summary>
public static class PdfiumLibrary
{
    /// <summary>Global monitor guarding all PDFium calls across the process.</summary>
    public static readonly object Sync = new();

    private static bool _initialized;

    /// <summary>Initializes PDFium exactly once. Safe to call repeatedly.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Sync)
        {
            if (_initialized) return;
            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NoPdf.Core.Import;

/// <summary>
/// Caches the PDF that a non-PDF document (DjVu, comic archive) was converted into, so
/// reopening a file is instant instead of re-running the whole decode. Entries live in the
/// temp directory and are keyed by the source's path, size and timestamp, so editing or
/// replacing the original silently produces a different key rather than a stale hit.
/// </summary>
public static class ImportCache
{
    /// <summary>
    /// Bump this whenever conversion output changes — a fixed decoder, different compression,
    /// new page sizing. Without it a cached PDF produced by an older build would be served
    /// forever, so users would keep seeing a bug that has already been fixed. Old entries age
    /// out on their own once nothing references them.
    /// </summary>
    private const int FormatVersion = 2;

    /// <summary>Entries untouched for this long are removed. Long enough that a library you
    /// browse regularly stays warm, short enough that temp doesn't grow without bound.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Total budget for the cache; the oldest entries go first once it is exceeded.</summary>
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    private static string CacheDir => Path.Combine(Path.GetTempPath(), "NoPdf", "import-cache");

    /// <summary>The cached conversion for this file, or null if there isn't a current one.</summary>
    public static byte[]? TryGet(string sourcePath)
    {
        try
        {
            var file = new FileInfo(CacheFile(sourcePath));
            if (!file.Exists || file.Length == 0) return null;
            var bytes = File.ReadAllBytes(file.FullName);
            // A truncated entry (interrupted write, full disk) must not be served as a document.
            if (bytes.Length < 5 || bytes[0] != '%') { TryDelete(file.FullName); return null; }
            try { File.SetLastWriteTimeUtc(file.FullName, DateTime.UtcNow); } catch { } // mark as used
            return bytes;
        }
        catch { return null; }   // the cache is an optimisation; never fail an open over it
    }

    /// <summary>Stores a conversion. Failures are ignored.</summary>
    public static void Store(string sourcePath, byte[] pdf)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string target = CacheFile(sourcePath);
            // Write beside the target and move into place, so a crash or a second instance
            // can never leave a half-written PDF to be read back as a real document.
            string temp = target + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, pdf);
            File.Move(temp, target, overwrite: true);
            Prune();
        }
        catch { }
    }

    /// <summary>
    /// Key = full path + size + last-write time. Path alone isn't enough (a re-scanned file
    /// keeps its name), and content hashing would mean reading the whole source — exactly the
    /// slow network read the cache exists to avoid.
    /// </summary>
    private static string CacheFile(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        string key = $"v{FormatVersion}|{Path.GetFullPath(sourcePath).ToLowerInvariant()}" +
                     $"|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(CacheDir, Convert.ToHexString(hash, 0, 16) + ".pdf");
    }

    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(CacheDir).GetFiles("*.pdf")
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            var cutoff = DateTime.UtcNow - MaxAge;
            long total = files.Sum(f => f.Length);

            foreach (var f in files)
            {
                bool stale = f.LastWriteTimeUtc < cutoff;
                if (!stale && total <= MaxTotalBytes) break;   // list is oldest-first
                total -= f.Length;
                TryDelete(f.FullName);
            }

            // Sweep abandoned partial writes.
            foreach (var t in Directory.GetFiles(CacheDir, "*.tmp"))
                if (File.GetLastWriteTimeUtc(t) < DateTime.UtcNow - TimeSpan.FromHours(1)) TryDelete(t);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NoPdf.Core.Signing;

/// <summary>What a document's embedded digital signature claims, and whether it holds up.</summary>
/// <param name="Signer">Subject name from the signing certificate.</param>
/// <param name="SignedUtc">The signing time the signer claimed, if present.</param>
/// <param name="IntegrityChecked">Whether the byte range let us test integrity at all.
/// False means the signature is structurally malformed (see <paramref name="Note"/>).</param>
/// <param name="IntegrityOk">The signed bytes still hash to what the signature says —
/// nothing covered by it has been altered.</param>
/// <param name="CoversWholeFile">The signature covers the file right to its end. False
/// means bytes were appended after signing: a later signature/edit, or tampering.</param>
/// <param name="ChainTrusted">The signing certificate chains to a root this machine trusts.</param>
/// <param name="ChainStatus">Why the chain isn't trusted (e.g. self-signed), or "trusted".</param>
/// <param name="ModifiedAfterSigning">The file was rewritten after this signature was
/// applied (its byte range no longer lines up), so the signature no longer covers it.</param>
/// <param name="Note">A structural problem that stopped integrity being checked, or null.</param>
/// <param name="Error">Set when the signature couldn't be parsed at all.</param>
public sealed record SignatureInfo(
    string Signer,
    DateTime? SignedUtc,
    bool IntegrityChecked,
    bool IntegrityOk,
    bool CoversWholeFile,
    bool ChainTrusted,
    string ChainStatus,
    bool ModifiedAfterSigning = false,
    string? Note = null,
    string? Error = null)
{
    /// <summary>True only when nothing is wrong: intact, complete and trusted.</summary>
    public bool IsFullyValid => Error is null && IntegrityChecked && IntegrityOk && CoversWholeFile && ChainTrusted;

    /// <summary>The signature is definitely not valid for this file — content changed under
    /// it, the file was re-saved after signing, or the CMS is unreadable.</summary>
    public bool IsBroken => Error is not null || ModifiedAfterSigning || (IntegrityChecked && !IntegrityOk);

    /// <summary>One line fit for the signatures panel.</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
                return Signer is "(unknown signer)" or "(unknown)"
                    ? "cannot read signature: " + Error
                    : $"signed by {Signer}, but it could not be read: {Error}";
            if (ModifiedAfterSigning)
                return "INVALID — the document was changed after it was signed";
            if (!IntegrityChecked)
                return $"cannot verify — {Note ?? "malformed signature"}; certificate {ChainStatus}";
            if (!IntegrityOk)
                return "INVALID — the document was changed after it was signed";
            if (!CoversWholeFile)
                return $"intact, but more was added to the file after signing; certificate {ChainStatus}";
            return ChainTrusted
                ? "valid — intact and the certificate is trusted"
                : $"intact, but the certificate is not trusted ({ChainStatus})";
        }
    }
}

/// <summary>
/// Verifies the PKCS#7 signatures embedded in a PDF, without a third-party library.
///
/// A signature is a dictionary with a /Contents &lt;hex&gt; holding the detached PKCS#7,
/// and a /ByteRange [a b c d] naming the two file spans it signs — everything except that
/// /Contents string. Verifying means: decode the CMS from /Contents (that alone yields the
/// signer, the signing time and the certificate chain), then, when the byte range lines up
/// with the file, re-hash those two spans against the CMS to prove nothing changed.
///
/// The CMS is read from the /Contents delimiters, not from the byte-range gap, so a file
/// with malformed byte ranges (e.g. signed by a tool that rewrites the whole file each
/// save, shifting the offsets) still reports who signed it and when — it just can't have
/// its integrity confirmed.
/// </summary>
public static class SignatureVerifier
{
    public static IReadOnlyList<SignatureInfo> Verify(byte[] pdf)
    {
        var results = new List<SignatureInfo>();
        foreach (var (lt, gt) in FindContents(pdf))
        {
            try { results.Add(VerifyOne(pdf, lt, gt)); }
            catch (Exception ex)
            {
                results.Add(new SignatureInfo("(unknown)", null, false, false, false, false, "", Error: ex.Message));
            }
        }
        return results;
    }

    /// <summary>Verifies the signature whose /Contents hex sits between <paramref name="lt"/>
    /// ('&lt;') and <paramref name="gt"/> ('&gt;').</summary>
    private static SignatureInfo VerifyOne(byte[] pdf, int lt, int gt)
    {
        var der = HexToBytes(pdf, lt + 1, gt);
        var cms = new SignedCms();
        cms.Decode(der);

        var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        var cert = signer?.Certificate;
        string name = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? "(unknown signer)";
        // The CMS may carry a PKCS#9 signing-time; if not, the signature dictionary's
        // /M (D:…) date is the next best thing (many e-signing tools use only that).
        var when = SigningTime(signer) ?? FindModDateNear(pdf, lt, gt);

        bool trusted = false;
        string chainStatus = "no certificate";
        if (cert is not null) (trusted, chainStatus) = BuildChain(cert);

        // Pair this /Contents with its sibling /ByteRange. Without a usable one we can
        // report who signed and when, but not whether the bytes are intact.
        var br = FindByteRangeNear(pdf, lt, gt);
        if (br is not { } range)
            return new SignatureInfo(name, when, false, false, false, trusted, chainStatus,
                Note: "no byte range");

        var (a, b, c, d) = range;
        // The byte range must actually bracket THIS /Contents string (span 1 ends at the
        // '<', span 2 starts at the '>'), and stay inside the file. If it doesn't, the
        // offsets are stale — the file was re-saved after signing — so the signature no
        // longer covers this document.
        bool brackets = Math.Abs(a + b - lt) <= 1 && Math.Abs(c - (gt + 1)) <= 1;
        bool inBounds = a >= 0 && b >= 0 && c >= a + b && d >= 0 && (long)c + d <= pdf.Length;
        if (!brackets || !inBounds)
            return new SignatureInfo(name, when, false, false, false, trusted, chainStatus,
                ModifiedAfterSigning: true,
                Note: !inBounds ? "byte range runs past the end of the file"
                                : "byte range no longer matches the signed content");

        // Re-hash the two signed spans exactly as the signer saw them.
        var content = new byte[b + d];
        Buffer.BlockCopy(pdf, a, content, 0, b);
        Buffer.BlockCopy(pdf, c, content, b, d);

        bool integrity;
        try
        {
            var check = new SignedCms(new ContentInfo(content), detached: true);
            check.Decode(der);
            check.CheckSignature(verifySignatureOnly: true);
            integrity = true;
        }
        catch { integrity = false; }

        // Trailing whitespace after the last signed byte is normal; anything else is
        // content added after this signature.
        bool covers = TrailingIsBlank(pdf, c + d);

        return new SignatureInfo(name, when, true, integrity, covers, trusted, chainStatus);
    }

    private static (bool trusted, string status) BuildChain(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        // Offline: a revocation check would hang or fail on a machine with no network,
        // and "can't reach the CRL" is not the question being asked here.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        bool ok = chain.Build(cert);
        if (ok) return (true, "trusted");
        var reasons = chain.ChainStatus
            .Select(s => Humanize(s.Status))
            .Distinct()
            .ToList();
        return (false, reasons.Count > 0 ? string.Join(", ", reasons) : "untrusted");
    }

    private static string Humanize(X509ChainStatusFlags f) => f switch
    {
        X509ChainStatusFlags.UntrustedRoot => "self-signed or unknown issuer",
        X509ChainStatusFlags.NotTimeValid => "certificate expired or not yet valid",
        X509ChainStatusFlags.Revoked => "certificate revoked",
        X509ChainStatusFlags.NotSignatureValid => "certificate signature invalid",
        X509ChainStatusFlags.PartialChain => "issuer certificate missing",
        _ => f.ToString(),
    };

    private static DateTime? SigningTime(SignerInfo? signer)
    {
        if (signer is null) return null;
        foreach (var attr in signer.SignedAttributes)
            if (attr.Oid?.Value == "1.2.840.113549.1.9.5") // PKCS#9 signing-time
                foreach (var v in attr.Values)
                    if (v is Pkcs9SigningTime t) return t.SigningTime.ToUniversalTime();
        return null;
    }

    /// <summary>Reads the signature dictionary's /M (D:…) date near this /Contents, when
    /// the CMS itself carried no signing time.</summary>
    private static DateTime? FindModDateNear(byte[] pdf, int lt, int gt)
    {
        const int window = 512;
        return ScanModDate(pdf, Math.Max(0, lt - window), lt)
            ?? ScanModDate(pdf, gt, Math.Min(pdf.Length, gt + window));
    }

    private static DateTime? ScanModDate(byte[] pdf, int start, int end)
    {
        var key = Encoding.ASCII.GetBytes("/M");
        for (int i = start; i + key.Length < end; i++)
        {
            if (!Match(pdf, i, key)) continue;
            int p = i + key.Length;
            // "/M" must be its own key, not the start of "/Metadata" etc.
            if (p < pdf.Length && (pdf[p] is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')) continue;
            while (p < pdf.Length && IsSpace(pdf[p])) p++;
            if (p >= pdf.Length || pdf[p] != '(') continue;
            int close = Array.IndexOf(pdf, (byte)')', p, Math.Max(0, pdf.Length - p));
            if (close < 0) continue;
            var s = Encoding.ASCII.GetString(pdf, p + 1, close - p - 1);
            if (ParsePdfDate(s) is { } dt) return dt;
        }
        return null;
    }

    /// <summary>Parses a PDF date string "D:YYYYMMDDHHmmSS±HH'mm'" to UTC.</summary>
    private static DateTime? ParsePdfDate(string s)
    {
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        if (s.Length < 4) return null;
        try
        {
            int Get(int at, int len, int def) =>
                at + len <= s.Length && int.TryParse(s.Substring(at, len), out int v) ? v : def;

            int year = Get(0, 4, -1);
            if (year < 0) return null;
            int month = Get(4, 2, 1), day = Get(6, 2, 1);
            int hour = Get(8, 2, 0), min = Get(10, 2, 0), sec = Get(12, 2, 0);
            var local = new DateTime(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 31),
                Math.Clamp(hour, 0, 23), Math.Clamp(min, 0, 59), Math.Clamp(sec, 0, 59), DateTimeKind.Utc);

            // Optional zone: Z, or ±HH'mm'. Convert to UTC by subtracting the offset.
            if (s.Length > 14)
            {
                char sign = s[14];
                if (sign is '+' or '-')
                {
                    int oh = Get(15, 2, 0);
                    int omStart = 17 < s.Length && s[17] == '\'' ? 18 : 17;
                    int om = Get(omStart, 2, 0);
                    var off = new TimeSpan(oh, om, 0);
                    local += sign == '+' ? -off : off;
                }
            }
            return local;
        }
        catch { return null; }
    }

    /// <summary>Reads the hex digits in pdf[from..to) into bytes, dropping any DER padding
    /// (the /Contents blob is zero-padded out to the space the signer reserved).</summary>
    private static byte[] HexToBytes(byte[] pdf, int from, int to)
    {
        var sb = new StringBuilder(Math.Max(0, to - from));
        for (int i = from; i < to && i < pdf.Length; i++)
        {
            char ch = (char)pdf[i];
            if (Uri.IsHexDigit(ch)) sb.Append(ch);
        }
        if (sb.Length < 2) throw new InvalidOperationException("empty /Contents");
        var bytes = Convert.FromHexString(sb.Length % 2 == 0 ? sb.ToString() : sb.ToString(0, sb.Length - 1));
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        return end == bytes.Length ? bytes : bytes[..end];
    }

    private static bool TrailingIsBlank(byte[] pdf, int from)
    {
        for (int i = from; i < pdf.Length; i++)
            if (pdf[i] is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0)) return false;
        return true;
    }

    /// <summary>Locates every "/Contents &lt;…&gt;" hex string that belongs to a signature
    /// (a /ByteRange sits within a few hundred bytes). Returns (ltIndex, gtIndex) pairs.</summary>
    private static IEnumerable<(int lt, int gt)> FindContents(byte[] pdf)
    {
        var key = Encoding.ASCII.GetBytes("/Contents");
        for (int i = 0; i + key.Length < pdf.Length; i++)
        {
            if (!Match(pdf, i, key)) continue;
            int p = i + key.Length;
            while (p < pdf.Length && IsSpace(pdf[p])) p++;
            if (p >= pdf.Length || pdf[p] != '<') continue;   // only hex-string /Contents
            int gt = Array.IndexOf(pdf, (byte)'>', p, Math.Max(0, pdf.Length - p));
            if (gt < 0) continue;
            // Only signature /Contents have a /ByteRange nearby; skip other hex strings.
            if (FindByteRangeNear(pdf, p, gt) is not null)
            {
                yield return (p, gt);
                i = gt;
            }
        }
    }

    /// <summary>Finds the /ByteRange belonging to the same signature dictionary as the
    /// /Contents at [lt, gt] — searching just after the '&gt;' and just before the '&lt;',
    /// since the two are always adjacent in the dictionary.</summary>
    private static (int a, int b, int c, int d)? FindByteRangeNear(byte[] pdf, int lt, int gt)
    {
        const int window = 512;
        return ScanByteRange(pdf, gt, Math.Min(pdf.Length, gt + window))
            ?? ScanByteRange(pdf, Math.Max(0, lt - window), lt);
    }

    private static (int a, int b, int c, int d)? ScanByteRange(byte[] pdf, int start, int end)
    {
        var key = Encoding.ASCII.GetBytes("/ByteRange");
        for (int i = start; i + key.Length < end; i++)
        {
            if (!Match(pdf, i, key)) continue;
            int p = i + key.Length;
            while (p < pdf.Length && IsSpace(pdf[p])) p++;
            if (p >= pdf.Length || pdf[p] != '[') continue;
            p++;

            var nums = new List<long>(4);
            while (p < pdf.Length && nums.Count < 4)
            {
                while (p < pdf.Length && IsSpace(pdf[p])) p++;
                int s = p;
                while (p < pdf.Length && pdf[p] >= '0' && pdf[p] <= '9') p++;
                if (p == s) break;
                if (long.TryParse(Encoding.ASCII.GetString(pdf, s, p - s), out long v)) nums.Add(v);
                else break;
            }
            if (nums.Count != 4) continue;
            // Keep them as ints for slicing; a range that overflows int is bogus anyway.
            if (nums.Any(n => n is < 0 or > int.MaxValue)) continue;
            return ((int)nums[0], (int)nums[1], (int)nums[2], (int)nums[3]);
        }
        return null;
    }

    private static bool Match(byte[] h, int at, byte[] needle)
    {
        if (at + needle.Length > h.Length) return false;
        for (int k = 0; k < needle.Length; k++) if (h[at + k] != needle[k]) return false;
        return true;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t';
}

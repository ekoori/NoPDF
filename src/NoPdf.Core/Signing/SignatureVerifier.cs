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
/// <param name="IntegrityOk">The signed bytes still hash to what the signature says —
/// nothing covered by it has been altered.</param>
/// <param name="CoversWholeFile">The signature covers the file right to its end. False
/// means bytes were appended after signing: a later signature/edit, or tampering.</param>
/// <param name="ChainTrusted">The signing certificate chains to a root this machine trusts.</param>
/// <param name="ChainStatus">Why the chain isn't trusted (e.g. self-signed), or "trusted".</param>
/// <param name="Error">Set when the signature couldn't be parsed at all.</param>
public sealed record SignatureInfo(
    string Signer,
    DateTime? SignedUtc,
    bool IntegrityOk,
    bool CoversWholeFile,
    bool ChainTrusted,
    string ChainStatus,
    string? Error = null)
{
    /// <summary>True only when nothing is wrong: intact, complete and trusted.</summary>
    public bool IsFullyValid => Error is null && IntegrityOk && CoversWholeFile && ChainTrusted;

    /// <summary>One line fit for the signatures panel.</summary>
    public string Summary => Error is not null
        ? "cannot read signature: " + Error
        : !IntegrityOk ? "INVALID — content changed after signing"
        : !CoversWholeFile ? $"intact, but the file was appended to after signing; certificate {ChainStatus}"
        : ChainTrusted ? "valid — intact and the certificate is trusted"
        : $"intact, but the certificate is not trusted ({ChainStatus})";
}

/// <summary>
/// Verifies the PKCS#7 signatures embedded in a PDF, without a third-party library.
///
/// A PDF signature dictionary holds a /ByteRange [a b c d] naming the two spans of the
/// file it signs — everything except the /Contents hex blob sitting in the gap, which is
/// the detached PKCS#7 itself. So verifying means: re-read those spans, re-check the CMS
/// against them, and build the signer's certificate chain.
/// </summary>
public static class SignatureVerifier
{
    public static IReadOnlyList<SignatureInfo> Verify(byte[] pdf)
    {
        var results = new List<SignatureInfo>();
        foreach (var (a, b, c, d) in FindByteRanges(pdf))
        {
            try { results.Add(VerifyOne(pdf, a, b, c, d)); }
            catch (Exception ex)
            {
                results.Add(new SignatureInfo("(unknown)", null, false, false, false, "", ex.Message));
            }
        }
        return results;
    }

    private static SignatureInfo VerifyOne(byte[] pdf, int a, int b, int c, int d)
    {
        // The two signed spans, rejoined exactly as the signer saw them.
        var signed = new byte[b + d];
        Buffer.BlockCopy(pdf, a, signed, 0, b);
        Buffer.BlockCopy(pdf, c, signed, b, d);

        var der = ReadContents(pdf, a + b, c);
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(der);

        bool integrity;
        try { cms.CheckSignature(verifySignatureOnly: true); integrity = true; }
        catch { integrity = false; }

        var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        var cert = signer?.Certificate;
        string name = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? "(unknown signer)";
        var when = SigningTime(signer);

        bool trusted = false;
        string status = "no certificate";
        if (cert is not null) (trusted, status) = BuildChain(cert);

        // Trailing whitespace after %%EOF is normal; anything more is appended content.
        bool covers = TrailingIsBlank(pdf, c + d);

        return new SignatureInfo(name, when, integrity, covers, trusted, status);
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

    /// <summary>The gap between the signed spans is the "&lt;hex…&gt;" /Contents literal.</summary>
    private static byte[] ReadContents(byte[] pdf, int from, int to)
    {
        int lt = Array.IndexOf(pdf, (byte)'<', from, Math.Max(0, to - from));
        if (lt < 0) throw new InvalidOperationException("no /Contents string in the byte-range gap");
        int gt = Array.IndexOf(pdf, (byte)'>', lt, Math.Max(0, to - lt));
        if (gt < 0) throw new InvalidOperationException("unterminated /Contents string");

        var sb = new StringBuilder(gt - lt);
        for (int i = lt + 1; i < gt; i++)
        {
            char ch = (char)pdf[i];
            if (Uri.IsHexDigit(ch)) sb.Append(ch);
        }
        // The blob is zero-padded out to the space the signer reserved; DER is
        // self-delimiting, so drop the padding before decoding.
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

    /// <summary>Finds every "/ByteRange [ a b c d ]" in the raw file.</summary>
    private static IEnumerable<(int a, int b, int c, int d)> FindByteRanges(byte[] pdf)
    {
        var key = Encoding.ASCII.GetBytes("/ByteRange");
        for (int i = 0; i + key.Length < pdf.Length; i++)
        {
            if (!Match(pdf, i, key)) continue;
            int p = i + key.Length;
            while (p < pdf.Length && IsSpace(pdf[p])) p++;
            if (p >= pdf.Length || pdf[p] != '[') continue;
            p++;

            var nums = new List<int>(4);
            while (p < pdf.Length && nums.Count < 4)
            {
                while (p < pdf.Length && IsSpace(pdf[p])) p++;
                int start = p;
                while (p < pdf.Length && pdf[p] >= '0' && pdf[p] <= '9') p++;
                if (p == start) break;
                if (int.TryParse(Encoding.ASCII.GetString(pdf, start, p - start), out int v)) nums.Add(v);
                else break;
            }
            if (nums.Count != 4) continue;

            int a = nums[0], b = nums[1], c = nums[2], d = nums[3];
            // Reject anything that doesn't address real bytes rather than trusting the file.
            if (a < 0 || b < 0 || c < b || d < 0) continue;
            if ((long)a + b > pdf.Length || (long)c + d > pdf.Length) continue;
            yield return (a, b, c, d);
            i = p;
        }
    }

    private static bool Match(byte[] h, int at, byte[] needle)
    {
        for (int k = 0; k < needle.Length; k++) if (h[at + k] != needle[k]) return false;
        return true;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t';
}

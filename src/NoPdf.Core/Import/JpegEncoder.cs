using System;
using System.IO;

namespace NoPdf.Core.Import;

/// <summary>
/// A minimal baseline JPEG encoder (sequential DCT, Huffman, 4:2:0), written here because
/// .NET has no cross-platform image encoder and noPDF ships without native image libraries.
/// Used for photographic page images, where PNG is a poor fit: a scanned plate costs a few
/// hundred KB here versus a couple of MB as PNG, and PDF stores the bytes verbatim
/// (DCTDecode) instead of re-deflating them.
/// </summary>
public static class JpegEncoder
{
    /// <summary>Encodes tightly packed, top-down RGB to a baseline JPEG.</summary>
    /// <param name="rgb">Width*Height*3 bytes, R,G,B per pixel.</param>
    /// <param name="quality">1..100; ~85 is visually lossless for scans.</param>
    public static byte[] Encode(byte[] rgb, int width, int height, int quality = 85)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Empty image.");
        if (rgb.Length < (long)width * height * 3) throw new ArgumentException("Short RGB buffer.");

        quality = Math.Clamp(quality, 1, 100);
        // The usual IJG mapping from a 1..100 quality onto a scale factor for the tables.
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;

        var qY = ScaleTable(StdQuantLuma, scale);
        var qC = ScaleTable(StdQuantChroma, scale);

        var ms = new MemoryStream(width * height / 4 + 1024);
        WriteHeaders(ms, width, height, qY, qC);

        // Pre-divide by the AAN scale factors so the DCT below can skip its final scaling.
        var fY = ForwardScale(qY);
        var fC = ForwardScale(qC);

        var bits = new BitWriter(ms);
        int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;
        var blk = new float[64];
        var coef = new int[64];

        // 4:2:0 — one 16x16 MCU carries four luma blocks and one of each chroma block.
        for (int my = 0; my < height; my += 16)
        {
            for (int mx = 0; mx < width; mx += 16)
            {
                for (int b = 0; b < 4; b++)
                {
                    LoadLuma(rgb, width, height, mx + (b & 1) * 8, my + (b >> 1) * 8, blk);
                    Fdct(blk);
                    Quantize(blk, fY, coef);
                    EncodeBlock(bits, coef, ref prevDcY, DcLumaCodes, AcLumaCodes);
                }
                LoadChroma(rgb, width, height, mx, my, blk, cb: true);
                Fdct(blk); Quantize(blk, fC, coef);
                EncodeBlock(bits, coef, ref prevDcCb, DcChromaCodes, AcChromaCodes);

                LoadChroma(rgb, width, height, mx, my, blk, cb: false);
                Fdct(blk); Quantize(blk, fC, coef);
                EncodeBlock(bits, coef, ref prevDcCr, DcChromaCodes, AcChromaCodes);
            }
        }

        bits.Flush();
        ms.WriteByte(0xFF); ms.WriteByte(0xD9); // EOI
        return ms.ToArray();
    }

    // ---------------- pixel loading ----------------

    /// <summary>One 8x8 luma block, level-shifted to [-128,127]. Edge pixels are clamped so
    /// partial blocks at the right/bottom margin repeat rather than smear to black.</summary>
    private static void LoadLuma(byte[] rgb, int w, int h, int x0, int y0, float[] blk)
    {
        for (int y = 0; y < 8; y++)
        {
            int sy = Math.Min(y0 + y, h - 1);
            for (int x = 0; x < 8; x++)
            {
                int sx = Math.Min(x0 + x, w - 1);
                int p = (sy * w + sx) * 3;
                blk[y * 8 + x] = Luma(rgb[p], rgb[p + 1], rgb[p + 2]) - 128f;
            }
        }
    }

    /// <summary>One 8x8 chroma block covering a 16x16 area, box-averaged 2:1 both ways.</summary>
    private static void LoadChroma(byte[] rgb, int w, int h, int x0, int y0, float[] blk, bool cb)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float acc = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    int sy = Math.Min(y0 + y * 2 + dy, h - 1);
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(x0 + x * 2 + dx, w - 1);
                        int p = (sy * w + sx) * 3;
                        acc += cb ? Cb(rgb[p], rgb[p + 1], rgb[p + 2])
                                  : Cr(rgb[p], rgb[p + 1], rgb[p + 2]);
                    }
                }
                blk[y * 8 + x] = acc / 4f - 128f;
            }
        }
    }

    private static float Luma(byte r, byte g, byte b) => 0.299f * r + 0.587f * g + 0.114f * b;
    private static float Cb(byte r, byte g, byte b) => -0.168736f * r - 0.331264f * g + 0.5f * b + 128f;
    private static float Cr(byte r, byte g, byte b) => 0.5f * r - 0.418688f * g - 0.081312f * b + 128f;

    // ---------------- transform ----------------

    /// <summary>The AAN float forward DCT (as in libjpeg's jfdctflt): separable, and it leaves
    /// each coefficient scaled by a known constant that <see cref="ForwardScale"/> folds into
    /// the quantisation table.</summary>
    private static void Fdct(float[] d)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            // pass 0 walks rows (stride 1, step 8), pass 1 walks columns (stride 8, step 1).
            int stride = pass == 0 ? 1 : 8, step = pass == 0 ? 8 : 1;
            for (int c = 0; c < 8; c++)
            {
                int o = c * step;
                float t0 = d[o] + d[o + 7 * stride], t7 = d[o] - d[o + 7 * stride];
                float t1 = d[o + stride] + d[o + 6 * stride], t6 = d[o + stride] - d[o + 6 * stride];
                float t2 = d[o + 2 * stride] + d[o + 5 * stride], t5 = d[o + 2 * stride] - d[o + 5 * stride];
                float t3 = d[o + 3 * stride] + d[o + 4 * stride], t4 = d[o + 3 * stride] - d[o + 4 * stride];

                float t10 = t0 + t3, t13 = t0 - t3;
                float t11 = t1 + t2, t12 = t1 - t2;
                d[o] = t10 + t11;
                d[o + 4 * stride] = t10 - t11;
                float z1 = (t12 + t13) * 0.707106781f;
                d[o + 2 * stride] = t13 + z1;
                d[o + 6 * stride] = t13 - z1;

                t10 = t4 + t5; t11 = t5 + t6; t12 = t6 + t7;
                float z5 = (t10 - t12) * 0.382683433f;
                float z2 = 0.541196100f * t10 + z5;
                float z4 = 1.306562965f * t12 + z5;
                float z3 = t11 * 0.707106781f;
                float z11 = t7 + z3, z13 = t7 - z3;
                d[o + 5 * stride] = z13 + z2;
                d[o + 3 * stride] = z13 - z2;
                d[o + stride] = z11 + z4;
                d[o + 7 * stride] = z11 - z4;
            }
        }
    }

    private static void Quantize(float[] blk, float[] fq, int[] outCoef)
    {
        // Emit in zig-zag order, which is what the entropy coder expects.
        for (int i = 0; i < 64; i++)
        {
            float v = blk[ZigZag[i]] * fq[ZigZag[i]];
            outCoef[i] = (int)MathF.Round(v);
        }
    }

    private static float[] ForwardScale(byte[] q)
    {
        var f = new float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                f[y * 8 + x] = 1f / (q[y * 8 + x] * (float)(AanScale[y] * AanScale[x] * 8.0));
        return f;
    }

    private static readonly double[] AanScale =
    {
        1.0, 1.387039845, 1.306562965, 1.175875602,
        1.0, 0.785694958, 0.541196100, 0.275899379
    };

    // ---------------- entropy coding ----------------

    private static void EncodeBlock(BitWriter bits, int[] coef, ref int prevDc,
        (ushort code, byte len)[] dcTable, (ushort code, byte len)[] acTable)
    {
        int diff = coef[0] - prevDc;
        prevDc = coef[0];

        int s = Magnitude(diff);
        bits.Write(dcTable[s]);
        if (s > 0) bits.WriteBits(diff < 0 ? diff - 1 : diff, s);

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int v = coef[k];
            if (v == 0) { run++; continue; }
            while (run > 15) { bits.Write(acTable[0xF0]); run -= 16; } // ZRL
            int sz = Magnitude(v);
            bits.Write(acTable[(run << 4) | sz]);
            bits.WriteBits(v < 0 ? v - 1 : v, sz);
            run = 0;
        }
        if (run > 0) bits.Write(acTable[0x00]); // EOB
    }

    /// <summary>Number of bits needed to represent |v| in JPEG's signed encoding.</summary>
    private static int Magnitude(int v)
    {
        v = Math.Abs(v);
        int s = 0;
        while (v > 0) { s++; v >>= 1; }
        return s;
    }

    private sealed class BitWriter
    {
        private readonly Stream _s;
        private uint _acc;
        private int _n;
        public BitWriter(Stream s) => _s = s;

        public void Write((ushort code, byte len) c) => WriteRaw(c.code, c.len);

        public void WriteBits(int value, int len)
        {
            if (len == 0) return;
            WriteRaw((uint)(value & ((1 << len) - 1)), len);
        }

        private void WriteRaw(uint code, int len)
        {
            _acc = (_acc << len) | code;
            _n += len;
            while (_n >= 8)
            {
                byte b = (byte)(_acc >> (_n - 8));
                _s.WriteByte(b);
                if (b == 0xFF) _s.WriteByte(0x00); // byte stuffing
                _n -= 8;
            }
        }

        public void Flush()
        {
            if (_n > 0) WriteRaw((uint)((1 << (8 - _n)) - 1), 8 - _n); // pad with 1s
        }
    }

    // ---------------- container ----------------

    private static void WriteHeaders(Stream s, int w, int h, byte[] qY, byte[] qC)
    {
        Write(s, 0xFF, 0xD8);                                   // SOI
        Write(s, 0xFF, 0xE0, 0, 16);                            // APP0/JFIF
        s.Write(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00, 1, 1, 0, 0, 1, 0, 1, 0, 0 });

        Write(s, 0xFF, 0xDB, 0, 67); s.WriteByte(0x00); WriteZigZag(s, qY);
        Write(s, 0xFF, 0xDB, 0, 67); s.WriteByte(0x01); WriteZigZag(s, qC);

        Write(s, 0xFF, 0xC0, 0, 17, 8);                         // SOF0, 8-bit
        Write(s, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3);
        Write(s, 1, 0x22, 0);  // Y  , 2x2 sampling, quant table 0
        Write(s, 2, 0x11, 1);  // Cb , 1x1,          quant table 1
        Write(s, 3, 0x11, 1);  // Cr

        WriteHuffmanTable(s, 0x00, DcLumaBits, DcLumaVals);
        WriteHuffmanTable(s, 0x10, AcLumaBits, AcLumaVals);
        WriteHuffmanTable(s, 0x01, DcChromaBits, DcChromaVals);
        WriteHuffmanTable(s, 0x11, AcChromaBits, AcChromaVals);

        Write(s, 0xFF, 0xDA, 0, 12, 3);                         // SOS
        Write(s, 1, 0x00, 2, 0x11, 3, 0x11);
        Write(s, 0, 63, 0);
    }

    private static void WriteZigZag(Stream s, byte[] table)
    {
        for (int i = 0; i < 64; i++) s.WriteByte(table[ZigZag[i]]);
    }

    private static void WriteHuffmanTable(Stream s, byte id, byte[] bits, byte[] vals)
    {
        Write(s, 0xFF, 0xC4);
        int len = 3 + 16 + vals.Length;
        Write(s, (byte)(len >> 8), (byte)len, id);
        s.Write(bits, 0, 16);
        s.Write(vals, 0, vals.Length);
    }

    private static void Write(Stream s, params byte[] bytes) => s.Write(bytes, 0, bytes.Length);

    private static byte[] ScaleTable(byte[] baseTable, int scale)
    {
        var t = new byte[64];
        for (int i = 0; i < 64; i++)
            t[i] = (byte)Math.Clamp((baseTable[i] * scale + 50) / 100, 1, 255);
        return t;
    }

    // ---------------- standard tables (ITU T.81 Annex K) ----------------

    private static readonly byte[] StdQuantLuma =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68,109,103, 77,
        24, 35, 55, 64, 81,104,113, 92,
        49, 64, 78, 87,103,121,120,101,
        72, 92, 95, 98,112,100,103, 99
    };

    private static readonly byte[] StdQuantChroma =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    };

    private static readonly int[] ZigZag =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    private static readonly byte[] DcLumaBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcLumaVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] DcChromaBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcChromaVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] AcLumaBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] AcLumaVals =
    {
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
        0x22,0x71,0x14,0x32,0x81,0x91,0xa1,0x08,0x23,0x42,0xb1,0xc1,0x15,0x52,0xd1,0xf0,
        0x24,0x33,0x62,0x72,0x82,0x09,0x0a,0x16,0x17,0x18,0x19,0x1a,0x25,0x26,0x27,0x28,
        0x29,0x2a,0x34,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
        0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
        0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
        0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,
        0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,
        0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,0xe1,0xe2,
        0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    };

    private static readonly byte[] AcChromaBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] AcChromaVals =
    {
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
        0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xa1,0xb1,0xc1,0x09,0x23,0x33,0x52,0xf0,
        0x15,0x62,0x72,0xd1,0x0a,0x16,0x24,0x34,0xe1,0x25,0xf1,0x17,0x18,0x19,0x1a,0x26,
        0x27,0x28,0x29,0x2a,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,
        0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,
        0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x82,0x83,0x84,0x85,0x86,0x87,
        0x88,0x89,0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,
        0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,
        0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,
        0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    };

    /// <summary>Canonical Huffman codes derived from the bit-length counts, indexed by symbol.</summary>
    private static (ushort code, byte len)[] BuildCodes(byte[] bits, byte[] vals)
    {
        var table = new (ushort, byte)[256];
        ushort code = 0;
        int k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++)
                table[vals[k++]] = (code++, (byte)len);
            code <<= 1;
        }
        return table;
    }

    private static readonly (ushort code, byte len)[] DcLumaCodes = BuildCodes(DcLumaBits, DcLumaVals);
    private static readonly (ushort code, byte len)[] AcLumaCodes = BuildCodes(AcLumaBits, AcLumaVals);
    private static readonly (ushort code, byte len)[] DcChromaCodes = BuildCodes(DcChromaBits, DcChromaVals);
    private static readonly (ushort code, byte len)[] AcChromaCodes = BuildCodes(AcChromaBits, AcChromaVals);
}

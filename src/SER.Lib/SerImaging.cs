using System.Runtime.CompilerServices;

namespace SharpAstro.Ser;

/// <summary>How decoded linear values are mapped to display.</summary>
public enum SerStretchMode
{
    /// <summary>Direct value/max mapping. The planetary default (matches SER Player's gain 1 / gamma 1).</summary>
    Linear,

    /// <summary>Auto screen-transfer (linked, luma-driven midtones), for faint / low-contrast captures.</summary>
    AutoStf,
}

/// <summary>Bayer demosaic algorithm.</summary>
public enum SerDebayer
{
    /// <summary>Simple bilinear interpolation. Fastest; softer, with edge zipper / false colour.</summary>
    Bilinear,

    /// <summary>
    /// Malvar-He-Cutler gradient-corrected linear interpolation (2004): bilinear plus a cross-channel
    /// Laplacian correction. ~bilinear speed, far fewer edge artifacts, and (unlike AHD) it won't
    /// manufacture noise-detail on a single noisy planetary frame. The default.
    /// </summary>
    Mhc,
}

/// <summary>
/// CPU frame pipeline: decode one SER frame (mono / Bayer-demosaic / RGB-deinterleave) to linear RGB,
/// then map to display RGBA8. Pure managed (no GPU dependency), so it serves every consumer -- the
/// headless <c>render</c>/export paths, the TUI, tests -- and is the source-of-truth the viewer's GPU
/// shader mirrors. Adequate for planetary frame sizes; the live viewer uses the GPU path.
/// </summary>
public static class SerImaging
{
    /// <summary>Reads frame <paramref name="index"/> and returns it as a width*height*4 RGBA8 buffer.</summary>
    public static byte[] RenderFrameToRgba(SerReader reader, int index,
        SerStretchMode mode = SerStretchMode.Linear, SerDebayer debayer = SerDebayer.Mhc)
    {
        var samples = new ushort[reader.SamplesPerFrame];
        reader.ReadFrame16(index, samples);
        var rgb = DecodeToLinearRgb(samples, reader.Width, reader.Height, reader.ColorId, reader.MaxSampleValue, debayer);
        return MapToRgba8(rgb, reader.Width, reader.Height, mode);
    }

    /// <summary>
    /// Decodes raw per-sample values (mono/Bayer mosaic, or interleaved RGB/BGR) to per-pixel linear
    /// RGB in <c>[0, 1]</c> (length <c>width*height*3</c>), normalising by <paramref name="maxSampleValue"/>.
    /// </summary>
    public static float[] DecodeToLinearRgb(ReadOnlySpan<ushort> samples, int width, int height,
        SerColorId colorId, int maxSampleValue, SerDebayer debayer = SerDebayer.Mhc)
    {
        var planes = colorId.PlaneCount;
        var expected = width * height * planes;
        if (samples.Length != expected)
        {
            throw new ArgumentException($"Expected {expected} samples for {width}x{height} {colorId}, got {samples.Length}.", nameof(samples));
        }

        var inv = 1f / maxSampleValue;
        var rgb = new float[width * height * 3];
        if (colorId.IsColor)
        {
            DecodeInterleaved(samples, colorId == SerColorId.Bgr, inv, rgb);
        }
        else if (colorId.IsBayer)
        {
            var offset = colorId.BayerOffset ?? (0, 0);
            if (debayer == SerDebayer.Mhc)
            {
                DebayerMhc(samples, width, height, offset, inv, rgb);
            }
            else
            {
                DebayerBilinear(samples, width, height, offset, inv, rgb);
            }
        }
        else
        {
            DecodeMono(samples, inv, rgb);
        }

        return rgb;
    }

    /// <summary>Maps linear RGB in <c>[0, 1]</c> (length <c>width*height*3</c>) to a width*height*4 RGBA8 buffer.</summary>
    public static byte[] MapToRgba8(ReadOnlySpan<float> linearRgb, int width, int height, SerStretchMode mode)
    {
        var rgba = new byte[width * height * 4];
        if (mode == SerStretchMode.AutoStf)
        {
            ComputeAutoStretch(linearRgb, out var shadow, out var midtones);
            var invRange = shadow < 1f ? 1f / (1f - shadow) : 1f;
            for (int p = 0, q = 0; p < linearRgb.Length; p += 3, q += 4)
            {
                rgba[q + 0] = StretchToByte(linearRgb[p + 0], shadow, invRange, midtones);
                rgba[q + 1] = StretchToByte(linearRgb[p + 1], shadow, invRange, midtones);
                rgba[q + 2] = StretchToByte(linearRgb[p + 2], shadow, invRange, midtones);
                rgba[q + 3] = 255;
            }
        }
        else
        {
            for (int p = 0, q = 0; p < linearRgb.Length; p += 3, q += 4)
            {
                rgba[q + 0] = ToByte(linearRgb[p + 0]);
                rgba[q + 1] = ToByte(linearRgb[p + 1]);
                rgba[q + 2] = ToByte(linearRgb[p + 2]);
                rgba[q + 3] = 255;
            }
        }

        return rgba;
    }

    /// <summary>Midtones transfer function (PixInsight/Siril MTF). <paramref name="m"/> is the midtones balance in (0, 1).</summary>
    public static float Mtf(float m, float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        if (x == m) return 0.5f;
        return ((m - 1f) * x) / (((2f * m) - 1f) * x - m);
    }

    private static byte ToByte(float v)
    {
        var i = (int)((v < 0f ? 0f : v > 1f ? 1f : v) * 255f + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    private static byte StretchToByte(float channel, float shadow, float invRange, float midtones)
    {
        var x = (channel - shadow) * invRange;
        x = x < 0f ? 0f : x > 1f ? 1f : x;
        return (byte)Math.Clamp((int)(Mtf(midtones, x) * 255f + 0.5f), 0, 255);
    }

    private static void DecodeMono(ReadOnlySpan<ushort> s, float inv, float[] rgb)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var v = s[i] * inv;
            rgb[i * 3] = v;
            rgb[(i * 3) + 1] = v;
            rgb[(i * 3) + 2] = v;
        }
    }

    private static void DecodeInterleaved(ReadOnlySpan<ushort> s, bool bgr, float inv, float[] rgb)
    {
        var pixels = s.Length / 3;
        for (var i = 0; i < pixels; i++)
        {
            var a = s[(i * 3) + 0] * inv;
            var b = s[(i * 3) + 1] * inv;
            var c = s[(i * 3) + 2] * inv;
            rgb[(i * 3) + 0] = bgr ? c : a; // R
            rgb[(i * 3) + 1] = b;           // G
            rgb[(i * 3) + 2] = bgr ? a : c; // B
        }
    }

    private static void DebayerBilinear(ReadOnlySpan<ushort> s, int w, int h, (int X, int Y) redOffset, float inv, float[] rgb)
    {
        var rx = redOffset.X;
        var ry = redOffset.Y;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var idx = (y * w) + x;
                var xRed = (x & 1) == rx;
                var yRed = (y & 1) == ry;

                float r, g, b;
                if (xRed && yRed) // red site
                {
                    r = s[idx];
                    g = AvgOrtho(s, w, h, x, y);
                    b = AvgDiag(s, w, h, x, y);
                }
                else if (!xRed && !yRed) // blue site
                {
                    b = s[idx];
                    g = AvgOrtho(s, w, h, x, y);
                    r = AvgDiag(s, w, h, x, y);
                }
                else // green site
                {
                    g = s[idx];
                    if (yRed)
                    {
                        r = AvgH(s, w, h, x, y);
                        b = AvgV(s, w, h, x, y);
                    }
                    else
                    {
                        b = AvgH(s, w, h, x, y);
                        r = AvgV(s, w, h, x, y);
                    }
                }

                rgb[(idx * 3) + 0] = Clamp01(r * inv);
                rgb[(idx * 3) + 1] = Clamp01(g * inv);
                rgb[(idx * 3) + 2] = Clamp01(b * inv);
            }
        }
    }

    // Malvar-He-Cutler (2004) gradient-corrected linear demosaic. Each missing channel is a 5x5 linear
    // filter applied to the raw mosaic; every kernel sums to 8 (÷8 => unity gain, no brightness shift).
    // The GPU fragment shader mirrors these exact coefficients.
    private static void DebayerMhc(ReadOnlySpan<ushort> s, int w, int h, (int X, int Y) redOffset, float inv, float[] rgb)
    {
        var rx = redOffset.X;
        var ry = redOffset.Y;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var idx = (y * w) + x;
                var c = (float)s[idx];
                var xRed = (x & 1) == rx;
                var yRed = (y & 1) == ry;

                float r, g, b;
                if (xRed && yRed) // red site
                {
                    r = c;
                    g = MhcGreen(s, w, h, x, y);
                    b = MhcDiagonal(s, w, h, x, y);
                }
                else if (!xRed && !yRed) // blue site
                {
                    b = c;
                    g = MhcGreen(s, w, h, x, y);
                    r = MhcDiagonal(s, w, h, x, y);
                }
                else // green site
                {
                    g = c;
                    if (yRed) // red row: red neighbours horizontal, blue vertical
                    {
                        r = MhcHorizontal(s, w, h, x, y);
                        b = MhcVertical(s, w, h, x, y);
                    }
                    else // blue row: red neighbours vertical, blue horizontal
                    {
                        r = MhcVertical(s, w, h, x, y);
                        b = MhcHorizontal(s, w, h, x, y);
                    }
                }

                rgb[(idx * 3) + 0] = Clamp01(r * inv);
                rgb[(idx * 3) + 1] = Clamp01(g * inv);
                rgb[(idx * 3) + 2] = Clamp01(b * inv);
            }
        }
    }

    // Green at a red/blue site (gain alpha = 1/2).
    private static float MhcGreen(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => ((4f * At(s, w, h, x, y))
            + (2f * (At(s, w, h, x, y - 1) + At(s, w, h, x, y + 1) + At(s, w, h, x - 1, y) + At(s, w, h, x + 1, y)))
            - (At(s, w, h, x, y - 2) + At(s, w, h, x, y + 2) + At(s, w, h, x - 2, y) + At(s, w, h, x + 2, y)))
           * 0.125f;

    // Red at a blue site / blue at a red site (gain gamma = 3/4): same-colour neighbours are the 4 diagonals.
    private static float MhcDiagonal(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => ((6f * At(s, w, h, x, y))
            + (2f * (At(s, w, h, x - 1, y - 1) + At(s, w, h, x + 1, y - 1) + At(s, w, h, x - 1, y + 1) + At(s, w, h, x + 1, y + 1)))
            - (1.5f * (At(s, w, h, x, y - 2) + At(s, w, h, x, y + 2) + At(s, w, h, x - 2, y) + At(s, w, h, x + 2, y))))
           * 0.125f;

    // Red/blue at a green site whose same-colour neighbours lie in the same ROW (gain beta = 5/8).
    private static float MhcHorizontal(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => ((5f * At(s, w, h, x, y))
            + (4f * (At(s, w, h, x - 1, y) + At(s, w, h, x + 1, y)))
            - (At(s, w, h, x - 1, y - 1) + At(s, w, h, x + 1, y - 1) + At(s, w, h, x - 1, y + 1) + At(s, w, h, x + 1, y + 1))
            + (0.5f * (At(s, w, h, x, y - 2) + At(s, w, h, x, y + 2)))
            - (At(s, w, h, x - 2, y) + At(s, w, h, x + 2, y)))
           * 0.125f;

    // Red/blue at a green site whose same-colour neighbours lie in the same COLUMN (transpose of the row case).
    private static float MhcVertical(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => ((5f * At(s, w, h, x, y))
            + (4f * (At(s, w, h, x, y - 1) + At(s, w, h, x, y + 1)))
            - (At(s, w, h, x - 1, y - 1) + At(s, w, h, x + 1, y - 1) + At(s, w, h, x - 1, y + 1) + At(s, w, h, x + 1, y + 1))
            + (0.5f * (At(s, w, h, x - 2, y) + At(s, w, h, x + 2, y)))
            - (At(s, w, h, x, y - 2) + At(s, w, h, x, y + 2)))
           * 0.125f;

    private static float AvgOrtho(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => (At(s, w, h, x - 1, y) + At(s, w, h, x + 1, y) + At(s, w, h, x, y - 1) + At(s, w, h, x, y + 1)) * 0.25f;

    private static float AvgDiag(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => (At(s, w, h, x - 1, y - 1) + At(s, w, h, x + 1, y - 1) + At(s, w, h, x - 1, y + 1) + At(s, w, h, x + 1, y + 1)) * 0.25f;

    private static float AvgH(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => (At(s, w, h, x - 1, y) + At(s, w, h, x + 1, y)) * 0.5f;

    private static float AvgV(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
        => (At(s, w, h, x, y - 1) + At(s, w, h, x, y + 1)) * 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float At(ReadOnlySpan<ushort> s, int w, int h, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= w) x = w - 1;
        if (y < 0) y = 0; else if (y >= h) y = h - 1;
        return s[(y * w) + x];
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private static void ComputeAutoStretch(ReadOnlySpan<float> rgb, out float shadow, out float midtones)
    {
        const int bins = 1024;
        const float target = 0.25f;
        const float shadowsClip = -2.8f;

        var pixels = rgb.Length / 3;
        var luma = new float[pixels];
        var hist = new int[bins];
        for (var i = 0; i < pixels; i++)
        {
            var l = (0.2126f * rgb[i * 3]) + (0.7152f * rgb[(i * 3) + 1]) + (0.0722f * rgb[(i * 3) + 2]);
            l = l < 0f ? 0f : l > 1f ? 1f : l;
            luma[i] = l;
            hist[(int)(l * (bins - 1))]++;
        }

        var median = PercentileFromHistogram(hist, pixels, 0.5f) / (float)(bins - 1);

        var devHist = new int[bins];
        for (var i = 0; i < pixels; i++)
        {
            devHist[(int)(Math.Abs(luma[i] - median) * (bins - 1))]++;
        }

        var avgDev = PercentileFromHistogram(devHist, pixels, 0.5f) / (float)(bins - 1) * 1.4826f;

        shadow = Math.Clamp(median + (shadowsClip * avgDev), 0f, 1f);
        midtones = Math.Clamp(Mtf(target, median - shadow), 0.001f, 0.999f);
    }

    private static int PercentileFromHistogram(int[] hist, int total, float fraction)
    {
        var threshold = (long)(total * fraction);
        long cumulative = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            cumulative += hist[i];
            if (cumulative >= threshold)
            {
                return i;
            }
        }

        return hist.Length - 1;
    }
}

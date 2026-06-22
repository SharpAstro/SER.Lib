using SharpAstro.Ser;

namespace SharpAstro.Ser.Viewer;

/// <summary>How a decoded frame's linear values are mapped to display.</summary>
public enum SerStretchMode
{
    /// <summary>Direct value/max mapping. The planetary default (matches SER Player's gain 1 / gamma 1).</summary>
    Linear,

    /// <summary>Auto screen-transfer (linked, luma-driven midtones), for faint / low-contrast captures.</summary>
    AutoStf,
}

/// <summary>
/// CPU frame pipeline for display: decode one SER frame (mono / Bayer-debayer / RGB-deinterleave) to
/// linear RGB, map it to display (linear by default; optional auto-STF), and pack to RGBA8. Adequate
/// for planetary frame sizes; the GPU path comes later.
/// </summary>
public static class SerImaging
{
    /// <summary>Decodes frame <paramref name="index"/> to a width*height*4 RGBA8 buffer using <paramref name="mode"/>.</summary>
    public static byte[] RenderFrameToRgba(SerReader reader, int index, SerStretchMode mode = SerStretchMode.Linear)
    {
        var w = reader.Width;
        var h = reader.Height;
        var samples = new ushort[reader.SamplesPerFrame];
        reader.ReadFrame16(index, samples);
        var inv = 1f / reader.MaxSampleValue;

        // 1) Decode to per-pixel linear RGB in [0, 1].
        var rgb = new float[w * h * 3];
        var colorId = reader.ColorId;
        if (colorId.IsColor)
        {
            DecodeInterleaved(samples, colorId == SerColorId.Bgr, inv, rgb);
        }
        else if (colorId.IsBayer)
        {
            DebayerBilinear(samples, w, h, colorId.BayerOffset ?? (0, 0), inv, rgb);
        }
        else
        {
            DecodeMono(samples, inv, rgb);
        }

        // 2) Map to display and pack RGBA8.
        var rgba = new byte[w * h * 4];
        if (mode == SerStretchMode.AutoStf)
        {
            ComputeAutoStretch(rgb, out var shadow, out var midtones);
            var invRange = shadow < 1f ? 1f / (1f - shadow) : 1f;
            for (int p = 0, q = 0; p < rgb.Length; p += 3, q += 4)
            {
                rgba[q + 0] = StretchToByte(rgb[p + 0], shadow, invRange, midtones);
                rgba[q + 1] = StretchToByte(rgb[p + 1], shadow, invRange, midtones);
                rgba[q + 2] = StretchToByte(rgb[p + 2], shadow, invRange, midtones);
                rgba[q + 3] = 255;
            }
        }
        else
        {
            for (int p = 0, q = 0; p < rgb.Length; p += 3, q += 4)
            {
                rgba[q + 0] = ToByte(rgb[p + 0]);
                rgba[q + 1] = ToByte(rgb[p + 1]);
                rgba[q + 2] = ToByte(rgb[p + 2]);
                rgba[q + 3] = 255;
            }
        }

        return rgba;
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
        var v = (int)(Mtf(midtones, x) * 255f + 0.5f);
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    /// <summary>Midtones transfer function (PixInsight/Siril MTF). <paramref name="m"/> is the midtones balance in (0, 1).</summary>
    internal static float Mtf(float m, float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        if (x == m) return 0.5f;
        return ((m - 1f) * x) / (((2f * m) - 1f) * x - m);
    }

    private static void DecodeMono(ushort[] s, float inv, float[] rgb)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var v = s[i] * inv;
            rgb[i * 3] = v;
            rgb[(i * 3) + 1] = v;
            rgb[(i * 3) + 2] = v;
        }
    }

    private static void DecodeInterleaved(ushort[] s, bool bgr, float inv, float[] rgb)
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

    private static void DebayerBilinear(ushort[] s, int w, int h, (int X, int Y) redOffset, float inv, float[] rgb)
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
                    if (yRed) // green in a red row: red is horizontal, blue vertical
                    {
                        r = AvgH(s, w, h, x, y);
                        b = AvgV(s, w, h, x, y);
                    }
                    else // green in a blue row
                    {
                        b = AvgH(s, w, h, x, y);
                        r = AvgV(s, w, h, x, y);
                    }
                }

                rgb[(idx * 3) + 0] = r * inv;
                rgb[(idx * 3) + 1] = g * inv;
                rgb[(idx * 3) + 2] = b * inv;
            }
        }
    }

    private static float At(ushort[] s, int w, int h, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= w) x = w - 1;
        if (y < 0) y = 0; else if (y >= h) y = h - 1;
        return s[(y * w) + x];
    }

    private static float AvgOrtho(ushort[] s, int w, int h, int x, int y)
        => (At(s, w, h, x - 1, y) + At(s, w, h, x + 1, y) + At(s, w, h, x, y - 1) + At(s, w, h, x, y + 1)) * 0.25f;

    private static float AvgDiag(ushort[] s, int w, int h, int x, int y)
        => (At(s, w, h, x - 1, y - 1) + At(s, w, h, x + 1, y - 1) + At(s, w, h, x - 1, y + 1) + At(s, w, h, x + 1, y + 1)) * 0.25f;

    private static float AvgH(ushort[] s, int w, int h, int x, int y)
        => (At(s, w, h, x - 1, y) + At(s, w, h, x + 1, y)) * 0.5f;

    private static float AvgV(ushort[] s, int w, int h, int x, int y)
        => (At(s, w, h, x, y - 1) + At(s, w, h, x, y + 1)) * 0.5f;

    private static void ComputeAutoStretch(float[] rgb, out float shadow, out float midtones)
    {
        const int bins = 1024;
        const float target = 0.25f;     // target background level
        const float shadowsClip = -2.8f; // sigma below the median

        var pixels = rgb.Length / 3;
        var luma = new float[pixels];
        var hist = new int[bins];
        for (var i = 0; i < pixels; i++)
        {
            // Rec.709 luma.
            var l = (0.2126f * rgb[i * 3]) + (0.7152f * rgb[(i * 3) + 1]) + (0.0722f * rgb[(i * 3) + 2]);
            l = l < 0f ? 0f : l > 1f ? 1f : l;
            luma[i] = l;
            hist[(int)(l * (bins - 1))]++;
        }

        var median = PercentileFromHistogram(hist, pixels, 0.5f) / (bins - 1);

        // MAD via a histogram of |luma - median|.
        var devHist = new int[bins];
        for (var i = 0; i < pixels; i++)
        {
            var d = Math.Abs(luma[i] - median);
            devHist[(int)(d * (bins - 1))]++;
        }

        var mad = PercentileFromHistogram(devHist, pixels, 0.5f) / (bins - 1);
        var avgDev = mad * 1.4826f; // normal-consistent estimator of sigma

        shadow = median + (shadowsClip * avgDev);
        shadow = shadow < 0f ? 0f : shadow > 1f ? 1f : shadow;

        var m = Mtf(target, median - shadow);
        midtones = m < 0.001f ? 0.001f : m > 0.999f ? 0.999f : m;
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

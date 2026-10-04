using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

public class SerImagingTests
{
    // A flat (constant) mosaic must demosaic to that same constant on every channel: this pins the
    // MHC kernels' unity gain (each 5x5 kernel sums to 8, ÷8 = 1) -- a coefficient/normalisation typo
    // would shift brightness or tint a channel and fail here.
    [Theory]
    [InlineData(SerDebayer.Mhc)]
    [InlineData(SerDebayer.Bilinear)]
    public void Debayer_ConstantMosaic_ReproducesConstantOnAllChannels(SerDebayer debayer)
    {
        const int w = 16, h = 12, max = 255;
        const ushort value = 100;
        var samples = new ushort[w * h];
        Array.Fill(samples, value);

        var rgb = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.BayerRGGB, max, debayer);

        var expected = value / (float)max;
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i].ShouldBe(expected, tolerance: 1e-4f);
        }
    }

    // Each colour flat at its own level (a sky with a pedestal a channel) must demosaic to those levels at EVERY pixel, the frame's
    // outer two rows and columns included: a tap outside the frame has to read its own colour. Repeating the edge sample read the
    // neighbouring colour there (tianwen #1258). Every pattern phase and odd and even sizes, so each edge is met by each colour.
    [Theory]
    [InlineData(SerDebayer.Mhc, SerColorId.BayerRGGB, 16, 12)]
    [InlineData(SerDebayer.Mhc, SerColorId.BayerBGGR, 15, 13)]
    [InlineData(SerDebayer.Mhc, SerColorId.BayerGRBG, 9, 10)]
    [InlineData(SerDebayer.Mhc, SerColorId.BayerGBRG, 10, 9)]
    [InlineData(SerDebayer.Bilinear, SerColorId.BayerRGGB, 16, 12)]
    [InlineData(SerDebayer.Bilinear, SerColorId.BayerBGGR, 15, 13)]
    [InlineData(SerDebayer.Bilinear, SerColorId.BayerGRBG, 9, 10)]
    [InlineData(SerDebayer.Bilinear, SerColorId.BayerGBRG, 10, 9)]
    public void Debayer_EachColourFlat_ReproducesItsLevelUpToTheFramesEdge(SerDebayer debayer, SerColorId pattern, int w, int h)
    {
        const int max = 255;
        const ushort red = 90, green = 60, blue = 120;
        var (rx, ry) = pattern switch
        {
            SerColorId.BayerRGGB => (0, 0),
            SerColorId.BayerGRBG => (1, 0),
            SerColorId.BayerGBRG => (0, 1),
            _ => (1, 1),
        };
        var samples = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (xRed, yRed) = ((x & 1) == rx, (y & 1) == ry);
                samples[(y * w) + x] = xRed && yRed ? red : !xRed && !yRed ? blue : green;
            }
        }

        var rgb = SerImaging.DecodeToLinearRgb(samples, w, h, pattern, max, debayer);

        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3].ShouldBe(red / (float)max, tolerance: 1e-5f, $"red at ({i % w}, {i / w})");
            rgb[(i * 3) + 1].ShouldBe(green / (float)max, tolerance: 1e-5f, $"green at ({i % w}, {i / w})");
            rgb[(i * 3) + 2].ShouldBe(blue / (float)max, tolerance: 1e-5f, $"blue at ({i % w}, {i / w})");
        }
    }

    [Fact]
    public void Mhc_KnownChannel_PassesThroughExactlyAtItsOwnSite()
    {
        // RGGB: red pixels = 200, everything else = 0. At a red site the R channel must equal the raw
        // red value exactly (the centre sample is used verbatim; only the missing channels interpolate).
        const int w = 8, h = 8, max = 255;
        var samples = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if ((x & 1) == 0 && (y & 1) == 0) // RGGB red site
                {
                    samples[(y * w) + x] = 200;
                }
            }
        }

        var rgb = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.BayerRGGB, max, SerDebayer.Mhc);

        // interior red site (2,2)
        var idx = (2 * w) + 2;
        rgb[idx * 3].ShouldBe(200f / max, tolerance: 1e-4f); // R passes through
    }

    [Fact]
    public void DecodeMono_MapsSampleOverMax()
    {
        const int w = 4, h = 2, max = 65535;
        var samples = new ushort[] { 0, 1000, 32768, 65535, 100, 200, 300, 400 };

        var rgb = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.Mono, max);

        rgb[0].ShouldBe(0f);
        rgb[1].ShouldBe(0f); // grey: r==g==b
        rgb[2].ShouldBe(0f);
        rgb[3].ShouldBe(1000f / max, tolerance: 1e-6f);
        rgb[(3 * 3) + 0].ShouldBe(1f); // 65535/65535
    }

    [Fact]
    public void DecodeRgbAndBgr_DeinterleaveCorrectly()
    {
        const int w = 2, h = 1, max = 255;
        // pixel0 = (10,20,30), pixel1 = (40,50,60) in stored order
        var samples = new ushort[] { 10, 20, 30, 40, 50, 60 };

        var rgb = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.Rgb, max);
        rgb[0].ShouldBe(10f / max, tolerance: 1e-6f);
        rgb[1].ShouldBe(20f / max, tolerance: 1e-6f);
        rgb[2].ShouldBe(30f / max, tolerance: 1e-6f);

        var bgr = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.Bgr, max);
        bgr[0].ShouldBe(30f / max, tolerance: 1e-6f); // R from the 3rd stored byte
        bgr[1].ShouldBe(20f / max, tolerance: 1e-6f); // G
        bgr[2].ShouldBe(10f / max, tolerance: 1e-6f); // B from the 1st stored byte
    }

    [Fact]
    public void MapToRgba8_Linear_RoundsLinearToBytes()
    {
        // 1 pixel, rgb = (0, 0.5, 1)
        var rgba = SerImaging.MapToRgba8([0f, 0.5f, 1f], 1, 1, SerStretchMode.Linear);
        rgba[0].ShouldBe((byte)0);
        rgba[1].ShouldBe((byte)128); // round(0.5*255)=128
        rgba[2].ShouldBe((byte)255);
        rgba[3].ShouldBe((byte)255); // alpha
    }

    [Fact]
    public void DecodeToLinearRgb_WrongSampleCount_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            SerImaging.DecodeToLinearRgb(new ushort[10], 4, 4, SerColorId.Mono, 255));
    }

    // A 5x5 kernel on a frame narrower than its reach mirrors more than once, down to a single sample.
    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    public void Mhc_SmallImage_DoesNotThrowAtEdges(int w, int h)
    {
        var samples = new ushort[w * h];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(i + 1);
        }
        var rgb = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.BayerRGGB, 255, SerDebayer.Mhc);
        rgb.Length.ShouldBe(w * h * 3);
        foreach (var v in rgb)
        {
            v.ShouldBeInRange(0f, 1f);
        }
    }
}

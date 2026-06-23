using System.IO.Compression;
using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

/// <summary>
/// Smoke test against a genuine capture (header from real software, real Bayer colour id, a real
/// timestamp trailer, real 8-bit Bayer pixel layout). It runs in CI off a small committed fixture: a
/// 2-frame slice of a 640x480 8-bit RGGB Jupiter capture, carved out with <see cref="SerReader.CutTo"/>
/// and gzipped (decompressed to a scratch file here, since <see cref="SerReader"/> memory-maps a path).
/// Point <c>SER_SAMPLE_FILE</c> at an uncompressed <c>.ser</c> to run it against any other capture.
/// </summary>
public class SerRealFileTests(ITestOutputHelper output)
{
    [Fact]
    public void DecodesRealSampleFile()
    {
        var envPath = Environment.GetEnvironmentVariable("SER_SAMPLE_FILE");
        var usingCommittedFixture = string.IsNullOrEmpty(envPath) || !File.Exists(envPath);

        using var scratch = new TempFile();
        string path;
        if (usingCommittedFixture)
        {
            var gz = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jupiter-sample.ser.gz");
            File.Exists(gz).ShouldBeTrue($"Committed fixture missing (expected next to the test assembly): {gz}");
            DecompressGzip(gz, scratch.Path);
            path = scratch.Path;
        }
        else
        {
            path = envPath!;
        }

        using var reader = SerReader.Open(path);

        output.WriteLine($"File:        {path}");
        output.WriteLine($"FileId:      '{reader.Header.FileId}' (valid={reader.Header.HasValidFileId})");
        output.WriteLine($"Dimensions:  {reader.Width}x{reader.Height}  {reader.ColorId}  {reader.PixelDepthPerPlane}-bit  ({reader.BytesPerSample} byte/sample, {reader.PlaneCount} plane(s))");
        output.WriteLine($"Frames:      {reader.FrameCount}  (frame = {reader.FrameSizeBytes} bytes)");
        output.WriteLine($"Endianness:  flag={reader.Header.LittleEndianFlag} -> dataLittleEndian={reader.Header.DataLittleEndian}");
        output.WriteLine($"Metadata:    observer='{reader.Header.Observer}' instrument='{reader.Header.Instrument}' telescope='{reader.Header.Telescope}'");
        output.WriteLine($"Timestamps:  has={reader.HasTimestamps}  fps={reader.FramesPerSecond:F2}");
        output.WriteLine($"Header time: local={reader.Header.LocalDateTime:u}  utc={reader.Header.UtcDateTime:u}");
        if (reader.HasTimestamps)
        {
            output.WriteLine($"First frame: {reader.Timestamps[0]:yyyy-MM-dd HH:mm:ss.fff} UT");
            output.WriteLine($"Last frame:  {reader.Timestamps[^1]:yyyy-MM-dd HH:mm:ss.fff} UT");
        }

        DumpFrameStats(reader, 0);
        DumpFrameStats(reader, reader.FrameCount / 2);
        DumpFrameStats(reader, reader.FrameCount - 1); // proves O(1) seek to the last frame

        reader.Header.HasValidFileId.ShouldBeTrue();
        reader.Width.ShouldBeGreaterThan(0);
        reader.Height.ShouldBeGreaterThan(0);
        reader.FrameCount.ShouldBeGreaterThan(0);

        if (usingCommittedFixture)
        {
            // Exact shape of the committed Jupiter slice -- guards the fixture (and the Cut that made it)
            // against silent corruption.
            reader.Width.ShouldBe(640);
            reader.Height.ShouldBe(480);
            reader.ColorId.ShouldBe(SerColorId.BayerRGGB);
            reader.PixelDepthPerPlane.ShouldBe(8);
            reader.FrameCount.ShouldBe(2);
            reader.HasTimestamps.ShouldBeTrue();
            reader.Timestamps[1].ShouldBeGreaterThan(reader.Timestamps[0]); // ascending capture order
            reader.FramesPerSecond.ShouldNotBeNull();
            reader.FramesPerSecond!.Value.ShouldBeGreaterThan(0);
        }
    }

    private void DumpFrameStats(SerReader reader, int index)
    {
        var samples = new ushort[reader.SamplesPerFrame];
        reader.ReadFrame16(index, samples);

        int min = int.MaxValue, max = int.MinValue;
        long sum = 0;
        foreach (var s in samples)
        {
            if (s < min) min = s;
            if (s > max) max = s;
            sum += s;
        }

        output.WriteLine($"Frame {index,6}: min={min} max={max} mean={(double)sum / samples.Length:F1} (sample ceiling {reader.MaxSampleValue})");
    }

    private static void DecompressGzip(string gzipPath, string outputPath)
    {
        using var input = File.OpenRead(gzipPath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = File.Create(outputPath);
        gzip.CopyTo(output);
    }
}

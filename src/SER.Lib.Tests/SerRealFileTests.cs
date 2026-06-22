using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

/// <summary>
/// Opt-in smoke test against a real capture. Set the <c>SER_SAMPLE_FILE</c> environment variable to a
/// <c>.ser</c> path to exercise the reader end-to-end (header, random-access seek, timestamps) on a
/// file produced by real capture software. Skipped in CI / when the variable is unset.
/// </summary>
public class SerRealFileTests(ITestOutputHelper output)
{
    [Fact]
    public void DecodesRealSampleFile()
    {
        var path = Environment.GetEnvironmentVariable("SER_SAMPLE_FILE");
        Assert.SkipUnless(!string.IsNullOrEmpty(path) && File.Exists(path),
            "Set SER_SAMPLE_FILE to a .ser path to run this smoke test.");

        using var reader = SerReader.Open(path!);

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
        DumpFrameStats(reader, reader.FrameCount - 1); // proves O(1) seek to the last frame of a large file

        reader.Header.HasValidFileId.ShouldBeTrue();
        reader.Width.ShouldBeGreaterThan(0);
        reader.Height.ShouldBeGreaterThan(0);
        reader.FrameCount.ShouldBeGreaterThan(0);
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
}

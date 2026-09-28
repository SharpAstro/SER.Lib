using System.Buffers.Binary;
using System.Text;
using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

/// <summary>
/// Tests for <see cref="SerReader.CropTo"/>: a window of every frame, with the source's header bytes (bar the width and
/// height), its trailer bytes and whatever follows them copied verbatim, so nothing the source said is re-encoded.
/// </summary>
public class SerCropTests
{
    // A source as a capture program writes one, not as SerWriter does: space-padded strings, a local start time ten hours
    // off UTC, a trailer in LOCAL time (the SER Player convention) with a reserved high bit set on one stamp, and bytes
    // appended after the trailer.
    private static byte[] Capture(SerColorId colorId, int width, int height, int depth, int frames, out long frameSize)
    {
        var bytesPerSample = depth <= 8 ? 1 : 2;
        frameSize = (long)width * height * colorId.PlaneCount * bytesPerSample;
        var utc = new DateTime(2024, 12, 15, 12, 33, 50, DateTimeKind.Utc).Ticks;
        var local = utc + TimeSpan.FromHours(10).Ticks;
        var header = SerHeader.Create(colorId, width, height, depth, frames, dateTimeTicks: local, dateTimeUtcTicks: utc + 4582,
            littleEndianFlag: 1);
        var tail = Encoding.ASCII.GetBytes("<meta/>");
        var file = new byte[SerHeader.Size + (frames * frameSize) + (frames * 8L) + tail.Length];
        header.Write(file);
        foreach (var (offset, text) in new[] { (42, "Observer"), (82, "ZWO ASI462MC"), (122, "telescope") })
        {
            var field = file.AsSpan(offset, 40);
            field.Fill((byte)' ');
            Encoding.ASCII.GetBytes(text).CopyTo(field);
        }
        for (var i = SerHeader.Size; i < SerHeader.Size + (frames * frameSize); i++)
        {
            file[i] = (byte)((i * 31) ^ (i >> 7));
        }
        var trailer = SerHeader.Size + (frames * frameSize);
        for (var f = 0; f < frames; f++)
        {
            var stamp = local + (f * TimeSpan.FromMilliseconds(2.2).Ticks);
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan((int)(trailer + (f * 8))), f == 1 ? stamp | (1L << 62) : stamp);
        }
        tail.CopyTo(file.AsSpan((int)(trailer + (frames * 8L))));
        return file;
    }

    [Theory]
    [InlineData(SerColorId.BayerRGGB, 8)]
    [InlineData(SerColorId.Mono, 16)]
    [InlineData(SerColorId.Rgb, 8)]
    public void CropTo_KeepsTheHeaderTheTrailerAndTheTailBytesAndCutsEachFramesWindow(SerColorId colorId, int depth)
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        const int width = 12, height = 10, frames = 5, cropWidth = 6, cropHeight = 4;
        var source = Capture(colorId, width, height, depth, frames, out var frameSize);
        File.WriteAllBytes(src.Path, source);
        var origins = new[] { (0, 0), (2, 2), (6, 6), (4, 0), (6, 2) };
        var asked = new List<int>();

        using (var reader = SerReader.Open(src.Path))
        {
            reader.CropTo(dst.Path, cropWidth, cropHeight, i => { asked.Add(i); return origins[i]; }, TestContext.Current.CancellationToken).ShouldBe(frames);
        }

        asked.ShouldBe([0, 1, 2, 3, 4]);
        var crop = File.ReadAllBytes(dst.Path);
        var pixelBytes = colorId.PlaneCount * (depth <= 8 ? 1 : 2);
        var cropFrame = cropWidth * cropHeight * pixelBytes;
        ((long)crop.Length).ShouldBe(SerHeader.Size + (frames * cropFrame) + (source.Length - SerHeader.Size - (frames * frameSize)));

        // The header: every byte the source's except the width and height.
        crop.AsSpan(0, 26).SequenceEqual(source.AsSpan(0, 26)).ShouldBeTrue();
        BinaryPrimitives.ReadInt32LittleEndian(crop.AsSpan(26)).ShouldBe(cropWidth);
        BinaryPrimitives.ReadInt32LittleEndian(crop.AsSpan(30)).ShouldBe(cropHeight);
        crop.AsSpan(34, SerHeader.Size - 34).SequenceEqual(source.AsSpan(34, SerHeader.Size - 34)).ShouldBeTrue();

        // Each frame: the source's window at its origin, row by row.
        for (var f = 0; f < frames; f++)
        {
            var (x, y) = origins[f];
            for (var row = 0; row < cropHeight; row++)
            {
                var from = SerHeader.Size + (f * frameSize) + ((((y + row) * width) + x) * pixelBytes);
                var to = SerHeader.Size + (f * cropFrame) + (row * cropWidth * pixelBytes);
                crop.AsSpan(to, cropWidth * pixelBytes).SequenceEqual(source.AsSpan((int)from, cropWidth * pixelBytes)).ShouldBeTrue();
            }
        }

        // The trailer and the bytes after it, as they were.
        var sourceTail = source.AsSpan((int)(SerHeader.Size + (frames * frameSize)));
        crop.AsSpan(SerHeader.Size + (frames * cropFrame)).SequenceEqual(sourceTail).ShouldBeTrue();

        // And so a reader decodes the same timestamps, from the same local-time trailer.
        using var original = SerReader.Open(src.Path);
        using var cropped = SerReader.Open(dst.Path);
        cropped.Timestamps.ShouldBe(original.Timestamps);
        cropped.Header.LocalDateTime.ShouldBe(original.Header.LocalDateTime);
        cropped.Header.Instrument.ShouldBe("ZWO ASI462MC");
    }

    [Fact]
    public void CropTo_RefusesAnOddOriginOnABayerMosaicAndLeavesNothing()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        File.WriteAllBytes(src.Path, Capture(SerColorId.BayerRGGB, 12, 10, 8, 3, out _));
        using var reader = SerReader.Open(src.Path);

        Should.Throw<ArgumentException>(() => reader.CropTo(dst.Path, 6, 4, i => i == 2 ? (3, 2) : (2, 2)))
            .Message.ShouldContain("re-phase");
        File.Exists(dst.Path).ShouldBeFalse();
    }

    [Fact]
    public void CropTo_TakesAnOddOriginOnMono()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        File.WriteAllBytes(src.Path, Capture(SerColorId.Mono, 12, 10, 8, 3, out _));
        using var reader = SerReader.Open(src.Path);

        reader.CropTo(dst.Path, 6, 4, _ => (3, 5), TestContext.Current.CancellationToken).ShouldBe(3);
    }

    [Theory]
    [InlineData(-2, 0)]
    [InlineData(0, -2)]
    [InlineData(8, 0)]
    [InlineData(0, 8)]
    public void CropTo_RefusesAWindowOffTheFrameAndLeavesNothing(int x, int y)
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        File.WriteAllBytes(src.Path, Capture(SerColorId.BayerRGGB, 12, 10, 8, 3, out _));
        using var reader = SerReader.Open(src.Path);

        Should.Throw<ArgumentOutOfRangeException>(() => reader.CropTo(dst.Path, 6, 4, i => i == 1 ? (x, y) : (0, 0)));
        File.Exists(dst.Path).ShouldBeFalse();
    }

    [Fact]
    public void CropTo_StopsOnCancellationAndLeavesNothing()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        File.WriteAllBytes(src.Path, Capture(SerColorId.Mono, 12, 10, 8, 3, out _));
        using var reader = SerReader.Open(src.Path);
        using var cts = new CancellationTokenSource();

        Should.Throw<OperationCanceledException>(() => reader.CropTo(dst.Path, 6, 4, i =>
        {
            if (i == 1)
            {
                cts.Cancel();
            }
            return (0, 0);
        }, cts.Token));
        File.Exists(dst.Path).ShouldBeFalse();
    }
}

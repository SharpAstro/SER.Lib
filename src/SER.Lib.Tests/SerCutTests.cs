using System.Buffers.Binary;
using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

/// <summary>
/// Tests for <see cref="SerReader.CutTo"/> / <see cref="SerReader.Cut"/>: a frame-range slice must
/// preserve geometry, colour mode, metadata, byte order, and per-frame timestamps, copying frame bytes
/// verbatim (loss-free for any bit depth / endianness).
/// </summary>
public class SerCutTests
{
    [Fact]
    public void CutTo_MidRange_CopiesFramesMetadataAndTimestamps()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        const int width = 4, height = 3, frames = 10;
        const int frameLen = width * height;
        var start = new DateTimeOffset(2024, 12, 15, 13, 4, 28, TimeSpan.Zero);
        var step = TimeSpan.FromMilliseconds(50);

        using (var w = new SerWriter(src.Path, width, height, SerColorId.BayerRGGB, pixelDepthPerPlane: 8,
            observer: "obs", instrument: "cam", telescope: "scope", luId: 7))
        {
            for (var f = 0; f < frames; f++)
            {
                var frame = new byte[frameLen];
                for (var p = 0; p < frameLen; p++)
                {
                    frame[p] = (byte)(f * 16 + p);
                }

                w.AppendFrame(frame, start + step * f);
            }
        }

        const int cutStart = 3, cutCount = 4;
        int written;
        using (var reader = SerReader.Open(src.Path))
        {
            written = reader.CutTo(dst.Path, cutStart, cutCount);
        }

        written.ShouldBe(cutCount);

        using var cut = SerReader.Open(dst.Path);
        cut.Width.ShouldBe(width);
        cut.Height.ShouldBe(height);
        cut.FrameCount.ShouldBe(cutCount);
        cut.ColorId.ShouldBe(SerColorId.BayerRGGB);
        cut.Header.Observer.ShouldBe("obs");
        cut.Header.Instrument.ShouldBe("cam");
        cut.Header.Telescope.ShouldBe("scope");
        cut.Header.LuId.ShouldBe(7);
        cut.HasTimestamps.ShouldBeTrue();

        for (var i = 0; i < cutCount; i++)
        {
            var raw = new byte[frameLen];
            cut.ReadFrameBytes(i, raw);
            for (var p = 0; p < frameLen; p++)
            {
                raw[p].ShouldBe((byte)((cutStart + i) * 16 + p)); // frames cutStart..cutStart+cutCount
            }

            cut.Timestamps[i].ShouldBe(start + step * (cutStart + i));
        }

        cut.FramesPerSecond.ShouldNotBeNull();
        cut.FramesPerSecond!.Value.ShouldBe(20.0, tolerance: 1e-6); // 50 ms spacing
    }

    [Fact]
    public void CutTo_BigEndian16_PreservesByteOrderVerbatim()
    {
        // A flag=1 (big-endian, per Match-SER-Player) 16-bit source: the cut must keep the bytes (and the
        // flag) verbatim, so the slice reads back the same host-order values without re-encoding.
        using var src = new TempFile();
        using var dst = new TempFile();
        var values = new ushort[] { 0x1234, 0xABCD, 0x00FF, 0xFF00 }; // 2 samples/frame, 2 frames
        var header = SerHeader.Create(SerColorId.Mono, width: 2, height: 1, pixelDepthPerPlane: 16,
            frameCount: 2, littleEndianFlag: 1);
        var file = new byte[SerHeader.Size + values.Length * 2];
        header.Write(file);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(SerHeader.Size + i * 2), values[i]);
        }

        File.WriteAllBytes(src.Path, file);

        using (var reader = SerReader.Open(src.Path))
        {
            reader.CutTo(dst.Path, startFrame: 1, count: 1);
        }

        using var cut = SerReader.Open(dst.Path);
        cut.Header.LittleEndianFlag.ShouldBe(1);     // source byte order kept verbatim
        cut.Header.DataLittleEndian.ShouldBeFalse();
        cut.FrameCount.ShouldBe(1);

        var samples = new ushort[2];
        cut.ReadFrame16(0, samples);
        samples[0].ShouldBe((ushort)0x00FF);          // frame 1's two samples, decoded host-order
        samples[1].ShouldBe((ushort)0xFF00);
    }

    [Fact]
    public void CutTo_NoTimestampSource_ProducesNoTrailer()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        using (var w = new SerWriter(src.Path, 2, 2, SerColorId.Mono, 8))
        {
            for (var f = 0; f < 4; f++)
            {
                w.AppendFrame(new byte[4]);
            }
        }

        using (var reader = SerReader.Open(src.Path))
        {
            reader.CutTo(dst.Path, startFrame: 1, count: 2);
        }

        using var cut = SerReader.Open(dst.Path);
        cut.FrameCount.ShouldBe(2);
        cut.HasTimestamps.ShouldBeFalse();
        cut.Header.DateTimeTicks.ShouldBe(0);
    }

    [Fact]
    public void Cut_StaticConvenience_FullRange_IsIdenticalCopy()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        const int width = 2, height = 2, frames = 6;
        const int frameLen = width * height * 3; // RGB, 3 interleaved planes

        using (var w = new SerWriter(src.Path, width, height, SerColorId.Rgb, 8))
        {
            for (var f = 0; f < frames; f++)
            {
                var frame = new byte[frameLen];
                for (var p = 0; p < frameLen; p++)
                {
                    frame[p] = (byte)(f * 20 + p);
                }

                w.AppendFrame(frame);
            }
        }

        var written = SerReader.Cut(src.Path, dst.Path, startFrame: 0, count: frames);
        written.ShouldBe(frames);

        using var cut = SerReader.Open(dst.Path);
        cut.PlaneCount.ShouldBe(3);
        cut.FrameCount.ShouldBe(frames);
        for (var f = 0; f < frames; f++)
        {
            var raw = new byte[frameLen];
            cut.ReadFrameBytes(f, raw);
            for (var p = 0; p < frameLen; p++)
            {
                raw[p].ShouldBe((byte)(f * 20 + p));
            }
        }
    }

    [Fact]
    public void CutTo_ZeroCount_WritesEmptyButValidFile()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        using (var w = new SerWriter(src.Path, 2, 2, SerColorId.Mono, 8))
        {
            for (var f = 0; f < 3; f++)
            {
                w.AppendFrame(new byte[4]);
            }
        }

        using var reader = SerReader.Open(src.Path);
        reader.CutTo(dst.Path, startFrame: 1, count: 0).ShouldBe(0);

        using var cut = SerReader.Open(dst.Path);
        cut.FrameCount.ShouldBe(0);
    }

    [Fact]
    public void CutTo_RangeOutsideSource_Throws()
    {
        using var src = new TempFile();
        using var dst = new TempFile();
        using (var w = new SerWriter(src.Path, 2, 2, SerColorId.Mono, 8))
        {
            for (var f = 0; f < 5; f++)
            {
                w.AppendFrame(new byte[4]);
            }
        }

        using var reader = SerReader.Open(src.Path);
        Should.Throw<ArgumentOutOfRangeException>(() => reader.CutTo(dst.Path, startFrame: 3, count: 5)); // 3+5 > 5
        Should.Throw<ArgumentOutOfRangeException>(() => reader.CutTo(dst.Path, startFrame: -1, count: 2));
        Should.Throw<ArgumentOutOfRangeException>(() => reader.CutTo(dst.Path, startFrame: 0, count: -1));
    }
}

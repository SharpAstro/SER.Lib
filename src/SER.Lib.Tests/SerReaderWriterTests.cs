using System.Buffers.Binary;
using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

public class SerReaderWriterTests
{
    [Fact]
    public void RoundTrip_Mono8_PreservesFramesAndHeader()
    {
        using var tmp = new TempFile();

        const int width = 4, height = 2, frames = 3;
        const int frameLen = width * height; // 8-bit mono -> 1 byte/sample

        using (var w = new SerWriter(tmp.Path, width, height, SerColorId.Mono, pixelDepthPerPlane: 8))
        {
            w.FrameSizeBytes.ShouldBe(frameLen);
            for (var f = 0; f < frames; f++)
            {
                var frame = new byte[frameLen];
                for (var p = 0; p < frameLen; p++)
                {
                    frame[p] = (byte)(f * 16 + p);
                }

                w.AppendFrame(frame);
            }
        }

        using var reader = SerReader.Open(tmp.Path);
        reader.Width.ShouldBe(width);
        reader.Height.ShouldBe(height);
        reader.FrameCount.ShouldBe(frames);
        reader.ColorId.ShouldBe(SerColorId.Mono);
        reader.BytesPerSample.ShouldBe(1);
        reader.Header.DataLittleEndian.ShouldBeTrue();
        reader.HasTimestamps.ShouldBeFalse();

        for (var f = 0; f < frames; f++)
        {
            var raw = new byte[frameLen];
            reader.ReadFrameBytes(f, raw);

            var samples = new ushort[frameLen];
            reader.ReadFrame16(f, samples);

            for (var p = 0; p < frameLen; p++)
            {
                raw[p].ShouldBe((byte)(f * 16 + p));
                samples[p].ShouldBe((ushort)(byte)(f * 16 + p));
            }
        }
    }

    [Fact]
    public void RoundTrip_WithTimestamps_RecoversUtcAndFps()
    {
        using var tmp = new TempFile();
        const int width = 2, height = 2, frames = 5;
        var start = new DateTimeOffset(2024, 12, 15, 13, 4, 28, TimeSpan.Zero);
        var step = TimeSpan.FromMilliseconds(100);

        using (var w = new SerWriter(tmp.Path, width, height, SerColorId.Mono, 8))
        {
            for (var f = 0; f < frames; f++)
            {
                w.AppendFrame(new byte[width * height], start + step * f);
            }
        }

        using var reader = SerReader.Open(tmp.Path);
        reader.HasTimestamps.ShouldBeTrue();
        reader.Timestamps.Length.ShouldBe(frames);
        for (var f = 0; f < frames; f++)
        {
            reader.Timestamps[f].ShouldBe(start + step * f);
        }

        reader.Header.UtcDateTime.ShouldBe(start.UtcDateTime);
        reader.FramesPerSecond.ShouldNotBeNull();
        reader.FramesPerSecond!.Value.ShouldBe(10.0, tolerance: 1e-6); // 100 ms spacing
    }

    [Fact]
    public void RoundTrip_NoTimestamps_IsV2Shaped()
    {
        using var tmp = new TempFile();
        using (var w = new SerWriter(tmp.Path, 2, 2, SerColorId.Mono, 8))
        {
            w.AppendFrame(new byte[4]);
            w.AppendFrame(new byte[4]);
        }

        using var reader = SerReader.Open(tmp.Path);
        reader.HasTimestamps.ShouldBeFalse();
        reader.Timestamps.IsDefaultOrEmpty.ShouldBeTrue();
        reader.FramesPerSecond.ShouldBeNull();
        reader.Header.DateTimeTicks.ShouldBe(0);
        reader.Header.LocalDateTime.ShouldBeNull();
    }

    [Fact]
    public void RoundTrip_Mono16_PreservesSampleValues()
    {
        using var tmp = new TempFile();
        const int width = 3, height = 2;
        var values = new ushort[] { 0, 1000, 4095, 32768, 60000, 65535 };

        using (var w = new SerWriter(tmp.Path, width, height, SerColorId.Mono, pixelDepthPerPlane: 16))
        {
            var bytes = new byte[width * height * 2];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]); // host writes LE
            }

            w.AppendFrame(bytes);
        }

        using var reader = SerReader.Open(tmp.Path);
        reader.BytesPerSample.ShouldBe(2);
        reader.MaxSampleValue.ShouldBe(65535);

        var samples = new ushort[width * height];
        reader.ReadFrame16(0, samples);
        samples.ShouldBe(values);
    }

    [Fact]
    public void RoundTrip_Rgb8_PreservesInterleavedPlanes()
    {
        using var tmp = new TempFile();
        const int width = 2, height = 2;
        const int frameLen = width * height * 3;

        var frame = new byte[frameLen];
        for (var i = 0; i < frameLen; i++)
        {
            frame[i] = (byte)(i * 7);
        }

        using (var w = new SerWriter(tmp.Path, width, height, SerColorId.Rgb, 8))
        {
            w.FrameSizeBytes.ShouldBe(frameLen);
            w.AppendFrame(frame);
        }

        using var reader = SerReader.Open(tmp.Path);
        reader.PlaneCount.ShouldBe(3);
        reader.SamplesPerFrame.ShouldBe(frameLen);

        var raw = new byte[frameLen];
        reader.ReadFrameBytes(0, raw);
        raw.ShouldBe(frame);
    }

    [Fact]
    public void Read_Mono12_ReportsCorrectMaxSampleValue()
    {
        using var tmp = new TempFile();
        using (var w = new SerWriter(tmp.Path, 2, 2, SerColorId.Mono, pixelDepthPerPlane: 12))
        {
            w.AppendFrame(new byte[2 * 2 * 2]);
        }

        using var reader = SerReader.Open(tmp.Path);
        reader.PixelDepthPerPlane.ShouldBe(12);
        reader.MaxSampleValue.ShouldBe(4095);
        reader.BytesPerSample.ShouldBe(2);
    }

    [Fact]
    public void Read_BigEndian16_DecodesViaInvertedFlag()
    {
        // Hand-build a flag=1 (big-endian, per Match-SER-Player) 16-bit file and confirm the reader
        // byte-swaps it back to the intended host-order values.
        using var tmp = new TempFile();
        var values = new ushort[] { 0x1234, 0xABCD };

        var header = SerHeader.Create(SerColorId.Mono, width: 2, height: 1, pixelDepthPerPlane: 16,
            frameCount: 1, littleEndianFlag: 1);
        var file = new byte[SerHeader.Size + values.Length * 2];
        header.Write(file);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(SerHeader.Size + i * 2), values[i]);
        }

        File.WriteAllBytes(tmp.Path, file);

        using var reader = SerReader.Open(tmp.Path);
        reader.Header.DataLittleEndian.ShouldBeFalse();

        var samples = new ushort[2];
        reader.ReadFrame16(0, samples);
        samples[0].ShouldBe((ushort)0x1234);
        samples[1].ShouldBe((ushort)0xABCD);
    }

    [Fact]
    public void RandomAccess_ReadsFramesInAnyOrder()
    {
        using var tmp = new TempFile();
        const int frames = 16;
        using (var w = new SerWriter(tmp.Path, 1, 1, SerColorId.Mono, 8))
        {
            for (var f = 0; f < frames; f++)
            {
                w.AppendFrame([(byte)(f * 3)]);
            }
        }

        using var reader = SerReader.Open(tmp.Path);
        var buf = new byte[1];

        foreach (var f in new[] { frames - 1, 0, 7, 3, frames - 2 })
        {
            reader.ReadFrameBytes(f, buf);
            buf[0].ShouldBe((byte)(f * 3));
        }
    }

    [Fact]
    public void LargeFile_SeeksToLastFrameViaMemoryMap()
    {
        using var tmp = new TempFile();
        const int width = 256, height = 256, frames = 200; // ~13 MB of frame data

        using (var w = new SerWriter(tmp.Path, width, height, SerColorId.Mono, 8))
        {
            var frame = new byte[width * height];
            for (var f = 0; f < frames; f++)
            {
                frame[0] = (byte)f; // marker in the first pixel
                w.AppendFrame(frame);
            }
        }

        using var reader = SerReader.Open(tmp.Path);
        var buf = new byte[width * height];

        reader.ReadFrameBytes(frames - 1, buf);
        buf[0].ShouldBe((byte)(frames - 1));

        reader.ReadFrameBytes(0, buf);
        buf[0].ShouldBe((byte)0);
    }

    [Fact]
    public void Open_TruncatedFrameData_Throws()
    {
        using var tmp = new TempFile();
        var header = SerHeader.Create(SerColorId.Mono, 4, 4, 8, frameCount: 10); // claims 10 frames...
        var file = new byte[SerHeader.Size + 4 * 4 * 3]; // ...but only ~3 frames of data present
        header.Write(file);
        File.WriteAllBytes(tmp.Path, file);

        Should.Throw<InvalidDataException>(() => SerReader.Open(tmp.Path));
    }

    [Fact]
    public void Open_FileTooSmall_Throws()
    {
        using var tmp = new TempFile();
        File.WriteAllBytes(tmp.Path, new byte[100]); // < 178-byte header

        Should.Throw<InvalidDataException>(() => SerReader.Open(tmp.Path));
    }

    [Fact]
    public void ReadFrame_IndexOutOfRange_Throws()
    {
        using var tmp = new TempFile();
        using (var w = new SerWriter(tmp.Path, 2, 2, SerColorId.Mono, 8))
        {
            w.AppendFrame(new byte[4]);
        }

        using var reader = SerReader.Open(tmp.Path);
        Should.Throw<ArgumentOutOfRangeException>(() => reader.ReadFrameBytes(1, new byte[4]));
        Should.Throw<ArgumentOutOfRangeException>(() => reader.ReadFrameBytes(-1, new byte[4]));
    }

    [Fact]
    public void ReadFrame_WrongDestinationSize_Throws()
    {
        using var tmp = new TempFile();
        using (var w = new SerWriter(tmp.Path, 2, 2, SerColorId.Mono, 8))
        {
            w.AppendFrame(new byte[4]);
        }

        using var reader = SerReader.Open(tmp.Path);
        Should.Throw<ArgumentException>(() => reader.ReadFrameBytes(0, new byte[3]));
        Should.Throw<ArgumentException>(() => reader.ReadFrame16(0, new ushort[3]));
    }

    [Fact]
    public void AppendFrame_InconsistentTimestamps_Throws()
    {
        using var tmp = new TempFile();
        using var w = new SerWriter(tmp.Path, 2, 2, SerColorId.Mono, 8);
        w.AppendFrame(new byte[4], DateTimeOffset.UnixEpoch);
        Should.Throw<InvalidOperationException>(() => w.AppendFrame(new byte[4])); // missing timestamp
    }
}

using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

public class SerHeaderTests
{
    [Fact]
    public void ParseWrite_RoundTripsAllFields()
    {
        var original = SerHeader.Create(
            SerColorId.BayerRGGB, width: 1936, height: 1216, pixelDepthPerPlane: 12, frameCount: 5000,
            observer: "S. Godelet", instrument: "ASI678MC", telescope: "C9.25",
            dateTimeTicks: 638_000_000_000_000_000L, dateTimeUtcTicks: 638_000_360_000_000_000L, luId: 7);

        Span<byte> buffer = stackalloc byte[SerHeader.Size];
        original.Write(buffer);
        var parsed = SerHeader.Parse(buffer);

        parsed.FileId.ShouldBe(SerHeader.FileIdValue);
        parsed.HasValidFileId.ShouldBeTrue();
        parsed.LuId.ShouldBe(7);
        parsed.ColorId.ShouldBe(SerColorId.BayerRGGB);
        parsed.Width.ShouldBe(1936);
        parsed.Height.ShouldBe(1216);
        parsed.PixelDepthPerPlane.ShouldBe(12);
        parsed.FrameCount.ShouldBe(5000);
        parsed.Observer.ShouldBe("S. Godelet");
        parsed.Instrument.ShouldBe("ASI678MC");
        parsed.Telescope.ShouldBe("C9.25");
        parsed.DateTimeTicks.ShouldBe(638_000_000_000_000_000L);
        parsed.DateTimeUtcTicks.ShouldBe(638_000_360_000_000_000L);
    }

    [Fact]
    public void HeaderSize_Is178()
    {
        // The whole format depends on this constant; pin it.
        SerHeader.Size.ShouldBe(178);
        SerHeader.FileIdValue.Length.ShouldBe(14);
    }

    [Theory]
    [InlineData(SerColorId.Mono, 8, 1, 1, 255)]
    [InlineData(SerColorId.Mono, 16, 2, 1, 65535)]
    [InlineData(SerColorId.Mono, 12, 2, 1, 4095)]
    [InlineData(SerColorId.BayerRGGB, 8, 1, 1, 255)]
    [InlineData(SerColorId.Rgb, 8, 1, 3, 255)]
    [InlineData(SerColorId.Bgr, 16, 2, 3, 65535)]
    public void ComputedProperties_AreConsistent(SerColorId colorId, int depth, int bytesPerSample, int planes, int maxSample)
    {
        var header = SerHeader.Create(colorId, width: 10, height: 4, pixelDepthPerPlane: depth, frameCount: 3);

        header.BytesPerSample.ShouldBe(bytesPerSample);
        header.PlaneCount.ShouldBe(planes);
        header.MaxSampleValue.ShouldBe(maxSample);
        header.FrameSizeBytes.ShouldBe(10L * 4 * planes * bytesPerSample);
    }

    [Fact]
    public void DataLittleEndian_FollowsMatchSerPlayerConvention()
    {
        // Flag 0 == little-endian data; flag 1 == big-endian (inverted vs the v3 spec text).
        SerHeader.Create(SerColorId.Mono, 2, 2, 16, 1, littleEndianFlag: 0).DataLittleEndian.ShouldBeTrue();
        SerHeader.Create(SerColorId.Mono, 2, 2, 16, 1, littleEndianFlag: 1).DataLittleEndian.ShouldBeFalse();
    }

    [Fact]
    public void LocalAndUtcDateTime_NullWhenUnset()
    {
        var header = SerHeader.Create(SerColorId.Mono, 2, 2, 8, 1);
        header.LocalDateTime.ShouldBeNull();
        header.UtcDateTime.ShouldBeNull();
    }

    [Fact]
    public void Strings_AreTruncatedToFortyBytesAndAsciiSanitised()
    {
        var longName = new string('A', 50);
        var header = SerHeader.Create(SerColorId.Mono, 2, 2, 8, 1, observer: longName, instrument: "café");

        Span<byte> buffer = stackalloc byte[SerHeader.Size];
        header.Write(buffer);
        var parsed = SerHeader.Parse(buffer);

        parsed.Observer.ShouldBe(new string('A', 40));   // truncated to the 40-byte field
        parsed.Instrument.ShouldBe("caf?");              // non-ASCII 'e-acute' replaced with '?'
    }
}

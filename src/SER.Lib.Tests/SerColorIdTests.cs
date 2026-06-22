using Shouldly;
using Xunit;

namespace SharpAstro.Ser.Tests;

public class SerColorIdTests
{
    [Theory]
    [InlineData(SerColorId.Mono, 1, false, false)]
    [InlineData(SerColorId.BayerRGGB, 1, false, true)]
    [InlineData(SerColorId.BayerBGGR, 1, false, true)]
    [InlineData(SerColorId.BayerMYYC, 1, false, true)]
    [InlineData(SerColorId.Rgb, 3, true, false)]
    [InlineData(SerColorId.Bgr, 3, true, false)]
    public void PlaneCount_IsColor_IsBayer_AreConsistent(SerColorId id, int planes, bool isColor, bool isBayer)
    {
        id.PlaneCount.ShouldBe(planes);
        id.IsColor.ShouldBe(isColor);
        id.IsBayer.ShouldBe(isBayer);
    }

    [Fact]
    public void BayerOffset_MapsRggbFamilyOnly()
    {
        SerColorId.BayerRGGB.BayerOffset.ShouldBe((0, 0));
        SerColorId.BayerGRBG.BayerOffset.ShouldBe((1, 0));
        SerColorId.BayerGBRG.BayerOffset.ShouldBe((0, 1));
        SerColorId.BayerBGGR.BayerOffset.ShouldBe((1, 1));

        SerColorId.Mono.BayerOffset.ShouldBeNull();
        SerColorId.BayerCYYM.BayerOffset.ShouldBeNull();
        SerColorId.Rgb.BayerOffset.ShouldBeNull();
    }
}

using DexStream.Core.Device;
using Xunit;

namespace DexStream.Core.Tests;

public class SamsungDeviceCatalogTests
{
    [Theory]
    [InlineData("SM-S938B", "Galaxy S25 Ultra")]
    [InlineData("SM-S938U", "Galaxy S25 Ultra")]
    [InlineData("SM-S931B", "Galaxy S25")]
    [InlineData("SM-S928B", "Galaxy S24 Ultra")]
    [InlineData("SM-F956B", "Galaxy Z Fold6")]
    [InlineData("sm-s918b", "Galaxy S23 Ultra")]
    public void Lookup_RecognisesKnownModels(string model, string expectedName)
    {
        DexSupportInfo info = SamsungDeviceCatalog.Lookup(model);

        Assert.Equal(DexSupportLevel.Supported, info.Level);
        Assert.Equal(expectedName, info.MarketingName);
    }

    [Fact]
    public void Lookup_TreatsAnUnknownSamsungModelAsWorthTrying()
    {
        DexSupportInfo info = SamsungDeviceCatalog.Lookup("SM-Z999X", "samsung");

        Assert.Equal(DexSupportLevel.Unknown, info.Level);
        Assert.NotNull(info.Notes);
    }

    [Fact]
    public void Lookup_MarksNonSamsungDevicesAsUnsupported()
    {
        DexSupportInfo info = SamsungDeviceCatalog.Lookup("Pixel 8 Pro", "Google");

        Assert.Equal(DexSupportLevel.Unsupported, info.Level);
    }

    [Fact]
    public void Lookup_HandlesAMissingModel()
    {
        DexSupportInfo info = SamsungDeviceCatalog.Lookup(null, "samsung");

        Assert.Equal(DexSupportLevel.Unknown, info.Level);
        Assert.Contains("did not report", info.Notes);
    }

    [Fact]
    public void IsSamsungVendor_MatchesTheRealVendorId()
    {
        Assert.True(SamsungDeviceCatalog.IsSamsungVendor(0x04E8));
        Assert.False(SamsungDeviceCatalog.IsSamsungVendor(0x18D1));
    }
}

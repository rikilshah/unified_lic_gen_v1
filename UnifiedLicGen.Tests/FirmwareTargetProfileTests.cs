using UnifiedLicGen.Models;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class FirmwareTargetProfileTests
{
    [Theory]
    [InlineData("A26050605", "asm")]
    [InlineData("S26050606", "stepper")]
    public void FromSerial_DetectsHardwareFromStablePrefix(string serial, string expectedProfile)
    {
        var profile = FirmwareTargetProfile.FromSerial(serial);

        Assert.NotNull(profile);
        Assert.Equal(expectedProfile, profile.Id);
    }

    [Theory]
    [InlineData("000000000")]
    [InlineData("X26050605")]
    [InlineData("")]
    public void FromSerial_RejectsUnknownOrMissingPrefix(string serial)
    {
        Assert.Null(FirmwareTargetProfile.FromSerial(serial));
    }
}

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

    [Theory]
    [InlineData("asm", true, true, "asm")]
    [InlineData("stepper", true, true, "stepper")]
    [InlineData("stepper", true, false, "asm")]
    [InlineData("asm", false, true, "stepper")]
    public void ResolveConnectedTarget_PreservesSelectionWhenBothCardsAreOnline(
        string selectedProfile,
        bool asmDetected,
        bool stepperDetected,
        string expectedProfile)
    {
        var selected = FirmwareTargetProfile.FromId(selectedProfile);

        var resolved = FirmwareTargetProfile.ResolveConnectedTarget(selected, asmDetected, stepperDetected);

        Assert.Equal(expectedProfile, resolved.Id);
    }
}

using UnifiedLicGen.Models;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class StepperIdentityTests
{
    [Fact]
    public void Decode_UsesFirmwareWordOrderAndReservedCustomerRegister()
    {
        var identity = new ushort[46];
        identity[0] = 0x0021;
        identity[1] = 0x003B;
        identity[2] = 0x5307;
        identity[3] = 0x4830;
        identity[4] = 0x3253;
        identity[5] = 0x2038;
        identity[6] = 0; // Firmware register 10 is reserved.
        identity[7] = 0x0025;
        identity[8] = 0x005C;
        identity[9] = 0x0052;
        identity[10] = 0x001A;
        identity[11] = 0x0060;
        for (var index = 12; index < 44; index++) identity[index] = (ushort)index;
        identity[44] = 1;
        identity[45] = 3;

        var metadata = new ushort[] { 0, 'S', 26, 5, 6, 6, 1 };
        var result = StepperIdentity.Decode(identity, metadata);

        Assert.Equal("003B00214830530720383253", result.DeviceId96);
        Assert.Equal("3792822696", result.CustomerId10);
        Assert.Equal("S26050606", result.SerialNumber);
        Assert.Equal("1.3", result.FirmwareVersion);
        Assert.Equal((ushort)1, result.ProductCode);
        Assert.Equal(128, result.PublicKeyRawHex.Length);
        Assert.Equal(130, result.PublicKeySec1Hex.Length);
        Assert.Equal(64, result.PublicKeyFingerprintSha256.Length);
    }
}

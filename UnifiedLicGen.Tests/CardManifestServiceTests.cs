using System.Security.Cryptography;
using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class CardManifestServiceTests
{
    private static StepperIdentity Card()
    {
        var raw = Convert.ToHexString(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        return new StepperIdentity(
            "003B00214830530720383253", "3792822696", "S26050606",
            raw, $"04{raw}", Convert.ToHexString(SHA256.HashData(Convert.FromHexString(raw))),
            1, 3, 0, 1);
    }

    [Fact]
    public void Validate_AllRequiredFieldsMatch_Authorizes()
    {
        var card = Card();
        var manifest = new CardManifest
        {
            SerialNumber = card.SerialNumber,
            DeviceId96 = card.DeviceId96,
            CustomerId = card.CustomerId10,
            PublicKeyRawHex = card.PublicKeyRawHex,
            FingerprintSha256 = card.PublicKeyFingerprintSha256,
            FirmwareMajor = 1,
            FirmwareMinor = 3,
            ProductCode = 1
        };

        var result = new CardManifestService().Validate(card, manifest);

        Assert.True(result.IsAuthorized);
        Assert.Equal("Manifest verified.", result.Reason);
    }

    [Fact]
    public void Validate_PublicKeyMismatch_DeniesClosed()
    {
        var card = Card();
        var manifest = new CardManifest
        {
            SerialNumber = card.SerialNumber,
            DeviceId96 = card.DeviceId96,
            CustomerId = card.CustomerId10,
            PublicKeyRawHex = new string('A', 128),
            FingerprintSha256 = card.PublicKeyFingerprintSha256
        };

        var result = new CardManifestService().Validate(card, manifest);

        Assert.False(result.IsAuthorized);
        Assert.False(result.PublicKeyMatches);
        Assert.Contains("public key mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}

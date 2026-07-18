using System.Security.Cryptography;
using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class CardManifestServiceTests
{
    private static StepperIdentity Card(string serial = "S26050606", byte keyOffset = 0)
    {
        var raw = Convert.ToHexString(Enumerable.Range(0, 64).Select(i => (byte)(i + keyOffset)).ToArray());
        return new StepperIdentity(
            keyOffset == 0 ? "003B00214830530720383253" : "001600035630530F20353150",
            keyOffset == 0 ? "3792822696" : "9892301055", serial,
            raw, $"04{raw}", Convert.ToHexString(SHA256.HashData(Convert.FromHexString(raw))),
            1, 3, 0, 1);
    }

    private static CardManifest Manifest(StepperIdentity card) => new()
    {
        SerialNumber = card.SerialNumber,
        DeviceId96 = card.DeviceId96,
        CustomerId = card.CustomerId10,
        PublicKeyRawHex = card.PublicKeyRawHex,
        FingerprintSha256 = card.PublicKeyFingerprintSha256
    };

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

    [Fact]
    public void ValidateConnectedCards_UsesIndependentManifestForEachCard()
    {
        var asm = Card("A26050603", 64);
        var stepper = Card("S26050603");
        var service = new CardManifestService();

        var onlyAsm = service.ValidateConnectedCards(asm, Manifest(asm), stepper, null);
        var both = service.ValidateConnectedCards(asm, Manifest(asm), stepper, Manifest(stepper));

        Assert.True(onlyAsm.AsmAuthorized);
        Assert.False(onlyAsm.StepperAuthorized);
        Assert.Equal(1, onlyAsm.AuthorizedCount);
        Assert.True(both.AsmAuthorized);
        Assert.True(both.StepperAuthorized);
        Assert.Equal(2, both.AuthorizedCount);
        Assert.Equal("asm", CardManifestService.ManifestProfile(Manifest(asm)));
        Assert.Equal("stepper", CardManifestService.ManifestProfile(Manifest(stepper)));
    }
}

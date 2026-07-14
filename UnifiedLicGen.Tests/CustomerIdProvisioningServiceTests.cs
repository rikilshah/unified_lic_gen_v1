using UnifiedLicGen.Services;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class CustomerIdProvisioningServiceTests
{
    [Fact]
    public void Generate_AlwaysReturnsTenDigitsWithoutTripleSubsequence()
    {
        var service = new CustomerIdProvisioningService();

        for (var index = 0; index < 1000; index++)
        {
            var value = service.Generate();
            Assert.True(CustomerIdProvisioningService.IsValid(value), value);
        }
    }

    [Theory]
    [InlineData("0123456789", true)]
    [InlineData("1100223344", true)]
    [InlineData("1231114567", false)]
    [InlineData("0001234567", false)]
    [InlineData("123456789", false)]
    [InlineData("123456789X", false)]
    public void IsValid_EnforcesTheAgreedSop(string value, bool expected) =>
        Assert.Equal(expected, CustomerIdProvisioningService.IsValid(value));

    [Theory]
    [InlineData("S26050606", true)]
    [InlineData("a26050605", true)]
    [InlineData("26050605", false)]
    [InlineData("S2605060", false)]
    [InlineData("S2605060X", false)]
    public void IsValidSerial_EnforcesXyymmddss(string value, bool expected) =>
        Assert.Equal(expected, CustomerIdProvisioningService.IsValidSerial(value));

    [Fact]
    public async Task LoadOrCreateAsync_ReusesFinalTruthForTheSameCard()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-Customer-{Guid.NewGuid():N}");
        try
        {
            var service = new CustomerIdProvisioningService();
            var first = await service.LoadOrCreateAsync("S26050606", "003B00214830530720383253", "stepper", root);
            var second = await service.LoadOrCreateAsync("S26050606", "003B00214830530720383253", "stepper", root);

            Assert.Equal(first.CustomerId, second.CustomerId);
            Assert.True(File.Exists(CustomerIdProvisioningService.GetSessionPath(first.SerialNumber, root)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadOrCreateAsync_RejectsAStoredSessionForDifferentHardware()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-Customer-{Guid.NewGuid():N}");
        try
        {
            var service = new CustomerIdProvisioningService();
            await service.LoadOrCreateAsync("S26050606", "003B00214830530720383253", "stepper", root);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.LoadOrCreateAsync("S26050606", "003B00214830530720383253", "asm", root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadOrCreateAsync_RejectsDifferentAssignedSerialForSamePhysicalDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-Customer-{Guid.NewGuid():N}");
        try
        {
            var service = new CustomerIdProvisioningService();
            await service.LoadOrCreateAsync("S26050606", "003B00214830530720383253", "stepper", root);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.LoadOrCreateAsync("S26050607", "003B00214830530720383253", "stepper", root));

            Assert.Contains("S26050606", error.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class FirmwareProvisioningServiceTests
{
    [Fact]
    public async Task PrepareIdentityHeadersAsync_BacksUpAndStagesAllIdentityHeaders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-Firmware-{Guid.NewGuid():N}");
        var include = Path.Combine(root, "Core", "Inc");
        Directory.CreateDirectory(include);
        var generatedHeader = Path.Combine(root, "generated-card_public_key.h");
        await File.WriteAllTextAsync(generatedHeader, "#define DEVICE_PUBKEY_WORD00 0xADB9U");
        await File.WriteAllTextAsync(Path.Combine(include, "card_public_key.h"), "old-key");
        await File.WriteAllTextAsync(Path.Combine(include, "customer_id_config.h"), "old-customer");
        await File.WriteAllTextAsync(Path.Combine(include, "serial_number_config.h"), "old-serial");

        try
        {
            var cdi = new CardIdentityCdi { SerialNumber = "S26050606", DeviceId = "003B00214830530720383253", CustomerId = "3792822696" };
            var service = new FirmwareProvisioningService(root, "missing-cmake", "missing-programmer");

            var result = await service.PrepareIdentityHeadersAsync(cdi, generatedHeader);

            Assert.Equal("#define DEVICE_PUBKEY_WORD00 0xADB9U", await File.ReadAllTextAsync(result.PublicKeyHeader));
            Assert.Contains("APP_CUST_ID_TEXT \"3792822696\"", await File.ReadAllTextAsync(result.CustomerIdHeader));
            Assert.Contains("APP_CUST_ID_LEGACY32", await File.ReadAllTextAsync(result.CustomerIdHeader));
            Assert.Contains("APP_SERIAL_NUMBER_TEXT \"S26050606\"", await File.ReadAllTextAsync(result.SerialNumberHeader));
            Assert.Equal("old-key", await File.ReadAllTextAsync(Path.Combine(result.BackupFolder, "card_public_key.h")));
            Assert.Equal("old-customer", await File.ReadAllTextAsync(Path.Combine(result.BackupFolder, "customer_id_config.h")));
            Assert.Equal("old-serial", await File.ReadAllTextAsync(Path.Combine(result.BackupFolder, "serial_number_config.h")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AsmProfile_UsesItsReleaseArtifact()
    {
        var service = new FirmwareProvisioningService(FirmwareTargetProfile.Asm);

        Assert.Equal("Release", service.BuildPreset);
        Assert.EndsWith(Path.Combine("build", "Release", "VCB240002_2_0.elf"), service.ElfPath);
    }

    [Fact]
    public async Task FlashAsync_RejectsWrongTypedSerialBeforeRunningProgrammer()
    {
        var cdi = new CardIdentityCdi { SerialNumber = "S26050606", DeviceId = "003B00214830530720383253", CustomerId = "3792822696" };
        var service = new FirmwareProvisioningService("missing-root", "missing-cmake", "missing-programmer");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.FlashAsync("S26050605", cdi));

        Assert.Contains("exactly match", error.Message);
    }
}

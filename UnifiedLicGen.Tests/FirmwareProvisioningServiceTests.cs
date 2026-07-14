using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using Xunit;
using System.Diagnostics;

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
            DeleteTestDirectory(root);
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
    public async Task RepositoryDefaultAndAssignedIdentityStages_KeepPublicKeyPhasesSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-Firmware-{Guid.NewGuid():N}");
        var include = Path.Combine(root, "Core", "Inc");
        Directory.CreateDirectory(include);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(include, "card_public_key.h"), "repository-default-key");
            await File.WriteAllTextAsync(Path.Combine(include, "customer_id_config.h"), "repository-default-customer");
            await File.WriteAllTextAsync(Path.Combine(include, "serial_number_config.h"), "repository-default-serial");
            RunGit(root, "init", "-b", "main");
            RunGit(root, "config", "user.email", "tests@example.invalid");
            RunGit(root, "config", "user.name", "Unified Tests");
            RunGit(root, "add", "Core/Inc");
            RunGit(root, "commit", "-m", "defaults");
            RunGit(root, "update-ref", "refs/remotes/origin/main", "HEAD");

            await File.WriteAllTextAsync(Path.Combine(include, "card_public_key.h"), "previous-device-key");
            var service = new FirmwareProvisioningService(root, "missing-cmake", "missing-programmer");
            await service.PrepareRepositoryDefaultsAsync("OLD000001");
            Assert.Equal("repository-default-key", (await File.ReadAllTextAsync(Path.Combine(include, "card_public_key.h"))).Trim());

            var cdi = new CardIdentityCdi { SerialNumber = "S26050606", DeviceId = "003B00214830530720383253", CustomerId = "3792822696" };
            await service.PrepareAssignedIdentityHeadersAsync(cdi);
            Assert.Equal("repository-default-key", (await File.ReadAllTextAsync(Path.Combine(include, "card_public_key.h"))).Trim());
            Assert.Contains("APP_CUST_ID_TEXT \"3792822696\"", await File.ReadAllTextAsync(Path.Combine(include, "customer_id_config.h")));
            Assert.Contains("APP_SERIAL_NUMBER_TEXT \"S26050606\"", await File.ReadAllTextAsync(Path.Combine(include, "serial_number_config.h")));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(FirmwareProvisioningService.DefaultGitPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    private static void DeleteTestDirectory(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task FlashAsync_RejectsWrongTypedSerialBeforeRunningProgrammer()
    {
        var cdi = new CardIdentityCdi { SerialNumber = "S26050606", DeviceId = "003B00214830530720383253", CustomerId = "3792822696" };
        var service = new FirmwareProvisioningService("missing-root", "missing-cmake", "missing-programmer");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.FlashAsync("S26050605", cdi));

        Assert.Contains("exactly match", error.Message);
    }

    [Fact]
    public async Task FlashDefaultAsync_RejectsWrongHardwareSerialFormat()
    {
        var service = new FirmwareProvisioningService("missing-root", "missing-cmake", "missing-programmer");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FlashDefaultAsync("A26050605", 'S'));

        Assert.Contains("SYYMMDDSS", error.Message);
    }

    [Fact]
    public async Task FlashDefaultAsync_AcceptsAnyValidHardwareSerialBeforeCheckingArtifact()
    {
        var service = new FirmwareProvisioningService("missing-root", "missing-cmake", "missing-programmer");

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.FlashDefaultAsync("S99123199", 'S'));
    }
}

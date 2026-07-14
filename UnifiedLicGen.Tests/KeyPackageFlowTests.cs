using System.Text.Json;
using KeyGeneratorUi.Services;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class KeyPackageFlowTests
{
    [Fact]
    public async Task ExportPackageAsync_WritesCompleteLicFilesPackageWithoutPrivateKeyByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-Key-{Guid.NewGuid():N}");
        try
        {
            var identity = new DeviceIdentity(
                "S26050606",
                "S26050606",
                "003B00214830530720383253",
                "3792822696");
            var key = KeyPackageService.GenerateKeyPackage();
            var options = new ManifestExportOptions("Stepper Control Card V2", "TEST_BATCH", "TEST", false);

            var result = await KeyPackageService.ExportPackageAsync(root, identity, key, options);

            Assert.Equal(Path.Combine(root, "lic_files"), result);
            var expected = new[]
            {
                "S26050606_manifest.json",
                "S26050606_pubkey_raw.hex",
                "S26050606_pubkey_sec1.hex",
                "S26050606_pubkey_fingerprint.txt",
                "S26050606_pubkey_registers_be.txt",
                "card_public_key.h",
                "README_v1_0.md"
            };
            foreach (var file in expected) Assert.True(File.Exists(Path.Combine(result, file)), file);
            Assert.Empty(Directory.GetFiles(result, "*private*", SearchOption.TopDirectoryOnly));

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "S26050606_manifest.json")));
            Assert.Equal("S26050606", manifest.RootElement.GetProperty("serial_no").GetString());
            Assert.Equal("003B00214830530720383253", manifest.RootElement.GetProperty("stm32_uid_96").GetString());
            Assert.Equal("3792822696", manifest.RootElement.GetProperty("cust_id").GetString());
            Assert.False(manifest.RootElement.TryGetProperty("private_key", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

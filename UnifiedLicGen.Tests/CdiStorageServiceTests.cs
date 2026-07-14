using System.Text.Json;
using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class CdiStorageServiceTests
{
    [Fact]
    public async Task SaveToDatabaseAsync_UsesPcbDatabaseSopAndExactSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnifiedLicGen-{Guid.NewGuid():N}");
        try
        {
            var cdi = new CardIdentityCdi
            {
                SerialNumber = "S26050606",
                DeviceId = "003B00214830530720383253",
                CustomerId = "3792822696"
            };

            var path = await new CdiStorageService().SaveToDatabaseAsync(cdi, root);

            Assert.Equal(Path.Combine(root, "S26050606", "S26050606_CDI.json"), path);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var properties = document.RootElement.EnumerateObject().ToArray();
            Assert.Equal(new[] { "serial_no", "devid", "custid" }, properties.Select(p => p.Name));
            Assert.Equal("S26050606", document.RootElement.GetProperty("serial_no").GetString());
            Assert.Equal("003B00214830530720383253", document.RootElement.GetProperty("devid").GetString());
            Assert.Equal("3792822696", document.RootElement.GetProperty("custid").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

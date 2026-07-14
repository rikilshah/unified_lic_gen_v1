using System.Text.Json;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed class CdiStorageService
{
    public const string DefaultDatabaseRoot = @"G:\My Drive\PCB_LIC_DB";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<string> SaveToDatabaseAsync(
        CardIdentityCdi cdi,
        string databaseRoot = DefaultDatabaseRoot,
        CancellationToken cancellationToken = default)
    {
        Validate(cdi);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);

        var cardFolder = Path.Combine(databaseRoot, cdi.SerialNumber);
        Directory.CreateDirectory(cardFolder);
        var filePath = Path.Combine(cardFolder, $"{cdi.SerialNumber}_CDI.json");
        var json = JsonSerializer.Serialize(cdi, JsonOptions);
        await File.WriteAllTextAsync(filePath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return filePath;
    }

    public string Serialize(CardIdentityCdi cdi)
    {
        Validate(cdi);
        return JsonSerializer.Serialize(cdi, JsonOptions);
    }

    private static void Validate(CardIdentityCdi cdi)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        if (cdi.SerialNumber.Length != 9 || !char.IsLetter(cdi.SerialNumber[0]) || !cdi.SerialNumber[1..].All(char.IsDigit))
            throw new InvalidOperationException("CDI serial_no must use XYYMMDDSS format.");
        if (cdi.DeviceId.Length != 24 || !cdi.DeviceId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("CDI devid must contain exactly 24 hexadecimal characters.");
        if (cdi.CustomerId.Length != 10 || !cdi.CustomerId.All(char.IsDigit))
            throw new InvalidOperationException("CDI custid must contain exactly 10 decimal digits.");
    }
}


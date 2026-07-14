using System.Security.Cryptography;
using System.Text.Json;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed class CustomerIdProvisioningService
{
    private const string SessionFileName = "customer_id_provisioning_session.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Generate()
    {
        Span<char> value = stackalloc char[10];
        for (var index = 0; index < value.Length; index++)
        {
            int digit;
            do
            {
                digit = RandomNumberGenerator.GetInt32(10);
            }
            while (index >= 2 && value[index - 1] == value[index - 2] && value[index - 1] == (char)('0' + digit));

            value[index] = (char)('0' + digit);
        }

        return new string(value);
    }

    public static bool IsValid(string? value) =>
        value is { Length: 10 } && value.All(char.IsDigit) &&
        !Enumerable.Range(0, 8).Any(index => value[index] == value[index + 1] && value[index] == value[index + 2]);

    public async Task<CustomerIdProvisioningSession> LoadOrCreateAsync(
        string serialNumber,
        string deviceId,
        string hardwareProfile,
        string databaseRoot = CdiStorageService.DefaultDatabaseRoot,
        CancellationToken cancellationToken = default)
    {
        var path = GetSessionPath(serialNumber, databaseRoot);
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<CustomerIdProvisioningSession>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Customer ID provisioning session is empty.");
            if (!string.Equals(existing.SerialNumber, serialNumber, StringComparison.Ordinal) ||
                !string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.HardwareProfile, hardwareProfile, StringComparison.OrdinalIgnoreCase) ||
                !IsValid(existing.CustomerId))
            {
                throw new InvalidDataException("Stored Customer ID session does not match this card identity or hardware profile.");
            }
            return existing;
        }

        var created = new CustomerIdProvisioningSession
        {
            SerialNumber = serialNumber,
            DeviceId = deviceId,
            CustomerId = Generate(),
            HardwareProfile = hardwareProfile,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await SaveAsync(created, databaseRoot, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public Task MarkFlashStartedAsync(CustomerIdProvisioningSession session, string databaseRoot = CdiStorageService.DefaultDatabaseRoot, CancellationToken cancellationToken = default) =>
        SaveAsync(session with { FlashStartedUtc = session.FlashStartedUtc ?? DateTimeOffset.UtcNow }, databaseRoot, cancellationToken);

    public Task MarkVerifiedAsync(CustomerIdProvisioningSession session, string databaseRoot = CdiStorageService.DefaultDatabaseRoot, CancellationToken cancellationToken = default) =>
        SaveAsync(session with { FlashStartedUtc = session.FlashStartedUtc ?? DateTimeOffset.UtcNow, VerifiedUtc = DateTimeOffset.UtcNow }, databaseRoot, cancellationToken);

    public static string GetSessionPath(string serialNumber, string databaseRoot = CdiStorageService.DefaultDatabaseRoot) =>
        Path.Combine(databaseRoot, serialNumber, SessionFileName);

    private static async Task SaveAsync(CustomerIdProvisioningSession session, string databaseRoot, CancellationToken cancellationToken)
    {
        if (!IsValid(session.CustomerId)) throw new InvalidDataException("Customer ID must contain 10 digits without three identical consecutive digits.");
        var path = GetSessionPath(session.SerialNumber, databaseRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(session, JsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

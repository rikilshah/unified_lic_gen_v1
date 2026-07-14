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

    public static string NormalizeSerial(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    public static bool IsValidSerial(string? value) =>
        NormalizeSerial(value) is { Length: 9 } serial && char.IsLetter(serial[0]) && serial[1..].All(char.IsDigit);

    public async Task<CustomerIdProvisioningSession> LoadOrCreateAsync(
        string serialNumber,
        string deviceId,
        string hardwareProfile,
        string databaseRoot = CdiStorageService.DefaultDatabaseRoot,
        CancellationToken cancellationToken = default)
    {
        serialNumber = NormalizeSerial(serialNumber);
        if (!IsValidSerial(serialNumber)) throw new InvalidDataException("Assigned serial must use XYYMMDDSS format, for example S26050606 or A26050605.");
        var existingForDevice = await LoadForDeviceAsync(deviceId, hardwareProfile, databaseRoot, cancellationToken).ConfigureAwait(false);
        if (existingForDevice is not null && !string.Equals(existingForDevice.SerialNumber, serialNumber, StringComparison.Ordinal))
            throw new InvalidDataException($"This device already has pending/final serial {existingForDevice.SerialNumber}. Enter that serial to recover its provisioning session.");

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

    public static async Task<CustomerIdProvisioningSession?> LoadForDeviceAsync(
        string deviceId,
        string hardwareProfile,
        string databaseRoot = CdiStorageService.DefaultDatabaseRoot,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(databaseRoot)) return null;
        foreach (var path in Directory.EnumerateFiles(databaseRoot, SessionFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = JsonSerializer.Deserialize<CustomerIdProvisioningSession>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException($"Provisioning session is empty: {path}");
            if (string.Equals(session.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.HardwareProfile, hardwareProfile, StringComparison.OrdinalIgnoreCase))
                return session;
        }
        return null;
    }
}

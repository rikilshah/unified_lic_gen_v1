using System.Text.Json;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed record ConnectedCardAuthorization(
    ManifestValidationResult? Asm,
    ManifestValidationResult? Stepper)
{
    public bool AsmAuthorized => Asm?.IsAuthorized == true;
    public bool StepperAuthorized => Stepper?.IsAuthorized == true;
    public bool AnyAuthorized => AsmAuthorized || StepperAuthorized;
    public int AuthorizedCount => (AsmAuthorized ? 1 : 0) + (StepperAuthorized ? 1 : 0);
}

public sealed class CardManifestService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<CardManifest> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<CardManifest>(json, Options)
            ?? throw new InvalidDataException("Manifest JSON is empty or invalid.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        manifest.SerialNumber = FirstString(document.RootElement, manifest.SerialNumber, "serial_no", "serial_number", "serialNumber");
        manifest.DeviceId96 = FirstString(document.RootElement, manifest.DeviceId96, "stm32_uid_96", "devid", "DEVID", "device_id");
        manifest.CustomerId = FirstString(document.RootElement, manifest.CustomerId, "cust_id", "custid", "CUSTID");
        return manifest;
    }

    public ManifestValidationResult Validate(StepperIdentity card, CardManifest manifest)
    {
        var serialValid = string.IsNullOrWhiteSpace(manifest.SerialNumber) ||
                          string.Equals(manifest.SerialNumber.Trim(), card.SerialNumber, StringComparison.OrdinalIgnoreCase);
        var expectedDevice = NormalizeHex(manifest.DeviceId96);
        var deviceValid = expectedDevice.Length == 24 && expectedDevice.All(Uri.IsHexDigit) &&
                          expectedDevice == NormalizeHex(card.DeviceId96);
        var expectedCustomer = new string(manifest.CustomerId.Where(char.IsDigit).ToArray());
        var customerValid = expectedCustomer.Length == 10 && expectedCustomer == card.CustomerId10;

        var expectedRaw = NormalizeHex(manifest.PublicKeyRawHex);
        if (string.IsNullOrWhiteSpace(expectedRaw))
        {
            var sec1 = NormalizeHex(manifest.PublicKeySec1Hex);
            if (sec1.Length == 130 && sec1.StartsWith("04", StringComparison.Ordinal)) expectedRaw = sec1[2..];
        }
        var publicKeyValid = expectedRaw.Length == 128 && expectedRaw.All(Uri.IsHexDigit) &&
                             expectedRaw == NormalizeHex(card.PublicKeyRawHex);

        var expectedFingerprint = NormalizeHex(manifest.FingerprintSha256);
        var fingerprintValid = expectedFingerprint.Length == 64 && expectedFingerprint.All(Uri.IsHexDigit) &&
                               expectedFingerprint == NormalizeHex(card.PublicKeyFingerprintSha256);
        var firmwareValid = (!manifest.FirmwareMajor.HasValue || manifest.FirmwareMajor == card.FirmwareMajor) &&
                            (!manifest.FirmwareMinor.HasValue || manifest.FirmwareMinor == card.FirmwareMinor) &&
                            (!manifest.HardwareRevision.HasValue || manifest.HardwareRevision == card.HardwareRevision) &&
                            (!manifest.ProductCode.HasValue || manifest.ProductCode == card.ProductCode);

        var failures = new List<string>();
        if (!serialValid) failures.Add("serial number mismatch");
        if (!deviceValid) failures.Add("DEVID mismatch or invalid stm32_uid_96");
        if (!customerValid) failures.Add("customer ID mismatch or invalid cust_id");
        if (!publicKeyValid) failures.Add("raw P-256 public key mismatch or invalid key");
        if (!fingerprintValid) failures.Add("public-key fingerprint mismatch or invalid fingerprint");
        if (!firmwareValid) failures.Add("firmware/hardware policy mismatch");

        return new ManifestValidationResult(serialValid, deviceValid, customerValid, publicKeyValid,
            fingerprintValid, firmwareValid, failures.Count == 0 ? "Manifest verified." : string.Join("; ", failures) + ".");
    }

    public ConnectedCardAuthorization ValidateConnectedCards(
        StepperIdentity? asmCard,
        CardManifest? asmManifest,
        StepperIdentity? stepperCard,
        CardManifest? stepperManifest) => new(
            asmCard is null || asmManifest is null ? null : Validate(asmCard, asmManifest),
            stepperCard is null || stepperManifest is null ? null : Validate(stepperCard, stepperManifest));

    public static string ManifestProfile(CardManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var profile = FirmwareTargetProfile.FromSerial(manifest.SerialNumber)?.Id;
        return profile ?? throw new InvalidDataException("Manifest serial must identify an ASM (A...) or Stepper (S...) card.");
    }

    private static string FirstString(JsonElement root, string current, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string NormalizeHex(string? value) => new((value ?? string.Empty)
        .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Where(c => !char.IsWhiteSpace(c) && c is not '_' and not '-' and not ':' and not ',')
        .Select(char.ToUpperInvariant).ToArray());
}

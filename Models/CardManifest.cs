using System.Text.Json.Serialization;

namespace UnifiedLicGen.Models;

public sealed class CardManifest
{
    [JsonPropertyName("serial_no")] public string SerialNumber { get; set; } = string.Empty;
    [JsonPropertyName("stm32_uid_96")] public string DeviceId96 { get; set; } = string.Empty;
    [JsonPropertyName("cust_id")] public string CustomerId { get; set; } = string.Empty;
    [JsonPropertyName("pubkey_p256_raw_hex")] public string PublicKeyRawHex { get; set; } = string.Empty;
    [JsonPropertyName("pubkey_p256_sec1_hex")] public string PublicKeySec1Hex { get; set; } = string.Empty;
    [JsonPropertyName("pubkey_fingerprint_sha256")] public string FingerprintSha256 { get; set; } = string.Empty;
    [JsonPropertyName("fw_major")] public ushort? FirmwareMajor { get; set; }
    [JsonPropertyName("fw_minor")] public ushort? FirmwareMinor { get; set; }
    [JsonPropertyName("hw_rev")] public ushort? HardwareRevision { get; set; }
    [JsonPropertyName("product_code")] public ushort? ProductCode { get; set; }
}

public sealed record ManifestValidationResult(
    bool SerialMatches,
    bool DeviceIdMatches,
    bool CustomerIdMatches,
    bool PublicKeyMatches,
    bool FingerprintMatches,
    bool FirmwarePolicyMatches,
    string Reason)
{
    public bool IsAuthorized => SerialMatches && DeviceIdMatches && CustomerIdMatches &&
                                PublicKeyMatches && FingerprintMatches && FirmwarePolicyMatches;
}


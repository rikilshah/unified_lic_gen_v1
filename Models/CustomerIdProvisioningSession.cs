using System.Text.Json.Serialization;

namespace UnifiedLicGen.Models;

public sealed record CustomerIdProvisioningSession
{
    [JsonPropertyName("serial_no")] public required string SerialNumber { get; init; }
    [JsonPropertyName("devid")] public required string DeviceId { get; init; }
    [JsonPropertyName("custid")] public required string CustomerId { get; init; }
    [JsonPropertyName("hardware")] public required string HardwareProfile { get; init; }
    [JsonPropertyName("created_utc")] public required DateTimeOffset CreatedUtc { get; init; }
    [JsonPropertyName("flash_started_utc")] public DateTimeOffset? FlashStartedUtc { get; init; }
    [JsonPropertyName("verified_utc")] public DateTimeOffset? VerifiedUtc { get; init; }
}

using System.Text.Json.Serialization;

namespace UnifiedLicGen.Models;

public sealed record CardIdentityCdi
{
    [JsonPropertyName("serial_no")]
    public required string SerialNumber { get; init; }

    [JsonPropertyName("devid")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("custid")]
    public required string CustomerId { get; init; }

    public static CardIdentityCdi FromStepperIdentity(StepperIdentity identity) => new()
    {
        SerialNumber = identity.SerialNumber,
        DeviceId = identity.DeviceId96,
        CustomerId = identity.CustomerId10
    };
}


using System.Security.Cryptography;
using System.Text;

namespace UnifiedLicGen.Models;

public sealed record StepperIdentity(
    string DeviceId96,
    string CustomerId10,
    string SerialNumber,
    string PublicKeyRawHex,
    string PublicKeySec1Hex,
    string PublicKeyFingerprintSha256,
    ushort FirmwareMajor,
    ushort FirmwareMinor,
    ushort HardwareRevision,
    ushort ProductCode)
{
    public string FirmwareVersion => $"{FirmwareMajor}.{FirmwareMinor}";

    public static StepperIdentity Decode(ushort[] identity, ushort[] metadata)
    {
        if (identity.Length != 46)
        {
            throw new ArgumentException("Expected input registers 4..49 (46 words).", nameof(identity));
        }

        if (metadata.Length != 7)
        {
            throw new ArgumentException("Expected input registers 52..58 (7 words).", nameof(metadata));
        }

        var uid0 = Combine(identity[0], identity[1]);
        var uid1 = Combine(identity[2], identity[3]);
        var uid2 = Combine(identity[4], identity[5]);

        // Firmware reserves register 10 and stores five two-digit chunks in 11..15.
        var customer = string.Concat(identity.Skip(7).Take(5).Select(word => (word % 100).ToString("D2")));
        var rawKey = WordsToHex(identity.AsSpan(12, 32));
        var rawKeyBytes = Convert.FromHexString(rawKey);
        var fingerprint = Convert.ToHexString(SHA256.HashData(rawKeyBytes));
        var prefix = (char)(metadata[1] & 0xFF);
        var serial = $"{prefix}{metadata[2] % 100:D2}{metadata[3] % 100:D2}{metadata[4] % 100:D2}{metadata[5] % 100:D2}";

        return new StepperIdentity(
            $"{uid0:X8}{uid1:X8}{uid2:X8}",
            customer,
            serial,
            rawKey,
            $"04{rawKey}",
            fingerprint,
            identity[44],
            identity[45],
            metadata[0],
            metadata[6]);
    }

    private static uint Combine(ushort low, ushort high) => ((uint)high << 16) | low;

    private static string WordsToHex(ReadOnlySpan<ushort> words)
    {
        var result = new StringBuilder(words.Length * 4);
        foreach (var word in words)
        {
            result.Append(word.ToString("X4"));
        }

        return result.ToString();
    }
}

public sealed record StepperLiveStatus(
    uint Position,
    uint ActiveCommand,
    ushort Status,
    ushort Fault,
    bool Ip1,
    bool Ip2,
    bool EncZ,
    bool EncZRaw);


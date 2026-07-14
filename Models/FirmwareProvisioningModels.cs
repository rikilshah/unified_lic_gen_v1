namespace UnifiedLicGen.Models;

public sealed record FirmwarePreparationResult(
    string BackupFolder,
    string PublicKeyHeader,
    string CustomerIdHeader,
    string SerialNumberHeader);

public sealed record ExternalCommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}


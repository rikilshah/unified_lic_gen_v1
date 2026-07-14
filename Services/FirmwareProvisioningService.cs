using System.Diagnostics;
using System.Text;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed class FirmwareProvisioningService
{
    public const string DefaultFirmwareRoot = @"D:\stm32_vscode\stepper_control_card_v2";
    public const string DefaultCmakePath = @"C:\ST\STM32CubeCLT_1.15.0\CMake\bin\cmake.exe";
    public const string DefaultProgrammerPath = @"C:\ST\STM32CubeCLT_1.15.0\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe";
    public const string DefaultGitPath = @"C:\Program Files\Git\cmd\git.exe";

    private readonly string _firmwareRoot;
    private readonly string _cmakePath;
    private readonly string _programmerPath;
    private readonly string _buildPreset;
    private readonly string _elfFileName;

    public FirmwareProvisioningService(
        FirmwareTargetProfile profile,
        string cmakePath = DefaultCmakePath,
        string programmerPath = DefaultProgrammerPath)
        : this(profile.LocalRoot, cmakePath, programmerPath, profile.BuildPreset, profile.ElfFileName)
    {
        Profile = profile;
    }

    public FirmwareProvisioningService(
        string firmwareRoot = DefaultFirmwareRoot,
        string cmakePath = DefaultCmakePath,
        string programmerPath = DefaultProgrammerPath,
        string buildPreset = "MinSizeRel",
        string elfFileName = "STEPPER_CONTROL_CARD_V2.elf")
    {
        _firmwareRoot = firmwareRoot;
        _cmakePath = cmakePath;
        _programmerPath = programmerPath;
        _buildPreset = buildPreset;
        _elfFileName = elfFileName;
    }

    public FirmwareTargetProfile? Profile { get; }
    public string FirmwareRoot => _firmwareRoot;
    public string BuildPreset => _buildPreset;
    public string ElfPath => Path.Combine(_firmwareRoot, "build", _buildPreset, _elfFileName);

    public async Task<FirmwarePreparationResult> PrepareIdentityHeadersAsync(
        CardIdentityCdi cdi,
        string generatedPublicKeyHeader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        if (!File.Exists(generatedPublicKeyHeader))
            throw new FileNotFoundException("Generate and export the manifest package before preparing firmware.", generatedPublicKeyHeader);

        var result = CreatePreparation(cdi.SerialNumber);

        File.Copy(generatedPublicKeyHeader, result.PublicKeyHeader, overwrite: true);
        await File.WriteAllTextAsync(result.CustomerIdHeader, BuildCustomerHeader(cdi.CustomerId), Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(result.SerialNumberHeader, BuildSerialHeader(cdi.SerialNumber), Encoding.ASCII, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<FirmwarePreparationResult> PrepareRepositoryDefaultsAsync(
        string backupIdentity,
        CancellationToken cancellationToken = default)
    {
        var result = CreatePreparation(backupIdentity);
        await RestoreRepositoryFileAsync("Core/Inc/card_public_key.h", result.PublicKeyHeader, cancellationToken).ConfigureAwait(false);
        await RestoreRepositoryFileAsync("Core/Inc/customer_id_config.h", result.CustomerIdHeader, cancellationToken).ConfigureAwait(false);
        await RestoreRepositoryFileAsync("Core/Inc/serial_number_config.h", result.SerialNumberHeader, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FirmwarePreparationResult> PrepareAssignedIdentityHeadersAsync(
        CardIdentityCdi cdi,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        var result = CreatePreparation(cdi.SerialNumber);
        await RestoreRepositoryFileAsync("Core/Inc/card_public_key.h", result.PublicKeyHeader, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(result.CustomerIdHeader, BuildCustomerHeader(cdi.CustomerId), Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(result.SerialNumberHeader, BuildSerialHeader(cdi.SerialNumber), Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ExternalCommandResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        var configure = await RunAsync(
            _cmakePath, ["--preset", _buildPreset], _firmwareRoot, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (!configure.Succeeded) return configure;

        var build = await RunAsync(
            _cmakePath, ["--build", "--preset", _buildPreset, "--clean-first"], _firmwareRoot, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        return new ExternalCommandResult(build.ExitCode, configure.Output + Environment.NewLine + build.Output);
    }

    public async Task<ExternalCommandResult> ProbeStLinkAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(_programmerPath,
            ["--connect", "port=swd", "mode=UR", "reset=HWrst", "-ob", "displ"],
            _firmwareRoot, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        if (result.Succeeded && ShowsProtectedRdp(result.Output))
        {
            return new ExternalCommandResult(
                2,
                result.Output + Environment.NewLine +
                "Target RDP protection is enabled. Unified Dashboard will not alter option bytes because unlocking may mass-erase flash.");
        }

        return result;
    }

    public async Task<ExternalCommandResult> FlashAsync(string confirmationSerial, CardIdentityCdi cdi, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(confirmationSerial?.Trim(), cdi.SerialNumber, StringComparison.Ordinal))
            throw new InvalidOperationException($"Flash confirmation must exactly match serial {cdi.SerialNumber}.");
        if (!File.Exists(ElfPath)) throw new FileNotFoundException("Built firmware ELF was not found. Build successfully before flashing.", ElfPath);

        var probe = await ProbeStLinkAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded) throw new InvalidOperationException($"ST-LINK probe failed.{Environment.NewLine}{probe.Output}");
        if (ShowsProtectedRdp(probe.Output))
            throw new InvalidOperationException("Target reports protected RDP option bytes. Automatic unlock is intentionally blocked because it may mass-erase flash.");

        return await RunAsync(_programmerPath,
            ["--connect", "port=swd", "mode=UR", "reset=HWrst", "--download", ElfPath, "-hardRst", "-rst", "--start"],
            _firmwareRoot, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
    }

    private static void BackupIfPresent(string source, string backupFolder)
    {
        if (File.Exists(source)) File.Copy(source, Path.Combine(backupFolder, Path.GetFileName(source)), overwrite: false);
    }

    private FirmwarePreparationResult CreatePreparation(string backupIdentity)
    {
        var includeFolder = Path.Combine(_firmwareRoot, "Core", "Inc");
        if (!Directory.Exists(includeFolder)) throw new DirectoryNotFoundException($"Firmware include folder not found: {includeFolder}");
        var safeIdentity = string.Concat(backupIdentity.Where(char.IsLetterOrDigit));
        if (string.IsNullOrEmpty(safeIdentity)) safeIdentity = "unknown";
        var backupFolder = Path.Combine(_firmwareRoot, ".unified-backups", safeIdentity, DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(backupFolder);
        var result = new FirmwarePreparationResult(
            backupFolder,
            Path.Combine(includeFolder, "card_public_key.h"),
            Path.Combine(includeFolder, "customer_id_config.h"),
            Path.Combine(includeFolder, "serial_number_config.h"));
        BackupIfPresent(result.PublicKeyHeader, backupFolder);
        BackupIfPresent(result.CustomerIdHeader, backupFolder);
        BackupIfPresent(result.SerialNumberHeader, backupFolder);
        return result;
    }

    private async Task RestoreRepositoryFileAsync(string repositoryPath, string targetPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(_firmwareRoot, ".git")))
            throw new DirectoryNotFoundException($"Firmware source is not a Git checkout: {_firmwareRoot}");
        var result = await RunAsync(
            DefaultGitPath, ["show", $"origin/main:{repositoryPath}"], _firmwareRoot, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException($"Could not load repository default {repositoryPath} from origin/main.{Environment.NewLine}{result.Output}");
        await File.WriteAllTextAsync(targetPath, result.Output, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static bool ShowsProtectedRdp(string output)
    {
        var normalized = output.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return normalized.Contains("RDP:0XBB", StringComparison.Ordinal) || normalized.Contains("RDP=0XBB", StringComparison.Ordinal) ||
               normalized.Contains("RDP:0XCC", StringComparison.Ordinal) || normalized.Contains("RDP=0XCC", StringComparison.Ordinal);
    }

    private static string BuildCustomerHeader(string customerId) => $$"""
        #ifndef CUSTOMER_ID_CONFIG_H
        #define CUSTOMER_ID_CONFIG_H

        #include <stdint.h>

        #define APP_CUST_ID_WORD_COUNT 6U
        #define APP_CUST_ID_TEXT "{{customerId}}"
        #define APP_CUST_ID_CHAR_COUNT ((uint16_t) (sizeof(APP_CUST_ID_TEXT) - 1U))
        #define APP_CUST_ID_DIGIT(index) ((uint64_t) ((uint8_t) APP_CUST_ID_TEXT[(index)] - (uint8_t) '0'))
        #define APP_CUST_ID_DEC2(index) ((uint16_t) ((APP_CUST_ID_DIGIT(index) * 10ULL) + APP_CUST_ID_DIGIT((index) + 1U)))
        #define APP_CUST_ID_VALUE_U64 \
            ((APP_CUST_ID_DIGIT(0) * 1000000000ULL) + \
             (APP_CUST_ID_DIGIT(1) * 100000000ULL) + \
             (APP_CUST_ID_DIGIT(2) * 10000000ULL) + \
             (APP_CUST_ID_DIGIT(3) * 1000000ULL) + \
             (APP_CUST_ID_DIGIT(4) * 100000ULL) + \
             (APP_CUST_ID_DIGIT(5) * 10000ULL) + \
             (APP_CUST_ID_DIGIT(6) * 1000ULL) + \
             (APP_CUST_ID_DIGIT(7) * 100ULL) + \
             (APP_CUST_ID_DIGIT(8) * 10ULL) + APP_CUST_ID_DIGIT(9))
        _Static_assert(APP_CUST_ID_CHAR_COUNT == 10U, "APP_CUST_ID_TEXT must be a 10-digit decimal string");
        #define APP_CUST_ID_WORD0 0x0000U
        #define APP_CUST_ID_WORD1 APP_CUST_ID_DEC2(0)
        #define APP_CUST_ID_WORD2 APP_CUST_ID_DEC2(2)
        #define APP_CUST_ID_WORD3 APP_CUST_ID_DEC2(4)
        #define APP_CUST_ID_WORD4 APP_CUST_ID_DEC2(6)
        #define APP_CUST_ID_WORD5 APP_CUST_ID_DEC2(8)
        #define APP_CUST_ID_LEGACY32 ((uint32_t) (APP_CUST_ID_VALUE_U64 & 0xFFFFFFFFULL))
        static const uint16_t APP_CUST_ID_WORDS[APP_CUST_ID_WORD_COUNT] = {
            APP_CUST_ID_WORD0, APP_CUST_ID_WORD1, APP_CUST_ID_WORD2,
            APP_CUST_ID_WORD3, APP_CUST_ID_WORD4, APP_CUST_ID_WORD5,
        };

        #endif /* CUSTOMER_ID_CONFIG_H */
        """;

    private static string BuildSerialHeader(string serial) => $$"""
        #ifndef SERIAL_NUMBER_CONFIG_H
        #define SERIAL_NUMBER_CONFIG_H

        #include <stdint.h>

        #define APP_SERIAL_WORD_COUNT 5U
        #define APP_SERIAL_NUMBER_TEXT "{{serial}}"
        #define APP_SERIAL_CHAR_COUNT ((uint16_t) (sizeof(APP_SERIAL_NUMBER_TEXT) - 1U))
        #define APP_SERIAL_DIGIT(index) ((uint16_t) ((uint8_t) APP_SERIAL_NUMBER_TEXT[(index)] - (uint8_t) '0'))
        #define APP_SERIAL_DEC2(index) ((uint16_t) ((APP_SERIAL_DIGIT(index) * 10U) + APP_SERIAL_DIGIT((index) + 1U)))
        _Static_assert(APP_SERIAL_CHAR_COUNT == 9U, "APP_SERIAL_NUMBER_TEXT must use format XYYMMDDSS");
        #define APP_SERIAL_WORD0 ((uint16_t) (uint8_t) APP_SERIAL_NUMBER_TEXT[0])
        #define APP_SERIAL_WORD1 APP_SERIAL_DEC2(1)
        #define APP_SERIAL_WORD2 APP_SERIAL_DEC2(3)
        #define APP_SERIAL_WORD3 APP_SERIAL_DEC2(5)
        #define APP_SERIAL_WORD4 APP_SERIAL_DEC2(7)
        static const uint16_t APP_SERIAL_WORDS[APP_SERIAL_WORD_COUNT] = {
            APP_SERIAL_WORD0, APP_SERIAL_WORD1, APP_SERIAL_WORD2,
            APP_SERIAL_WORD3, APP_SERIAL_WORD4,
        };

        #endif /* SERIAL_NUMBER_CONFIG_H */
        """;

    private static async Task<ExternalCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Required tool was not found.", executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Command exceeded {timeout.TotalSeconds:0} seconds: {Path.GetFileName(executable)}");
        }
        return new ExternalCommandResult(process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
    }
}

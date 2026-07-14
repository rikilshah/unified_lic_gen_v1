namespace UnifiedLicGen.Models;

public sealed record FirmwareTargetProfile(
    string Id,
    string DisplayName,
    string RepositoryUrl,
    string LocalRoot,
    string BuildPreset,
    string ElfFileName,
    char SerialPrefix)
{
    public string ElfPath => Path.Combine(LocalRoot, "build", BuildPreset, ElfFileName);

    public static FirmwareTargetProfile Stepper { get; } = new(
        "stepper", "Stepper Motion Card",
        "https://github.com/rikilshah/stepper_control_card_v2",
        @"D:\stm32_vscode\stepper_control_card_v2",
        "MinSizeRel", "STEPPER_CONTROL_CARD_V2.elf", 'S');

    public static FirmwareTargetProfile Asm { get; } = new(
        "asm", "ASM I/O Card",
        "https://github.com/rikilshah/VCB240002",
        @"D:\stm32_vscode\VCB240002",
        "Release", "VCB240002_2_0.elf", 'A');

    public static FirmwareTargetProfile FromId(string id) =>
        string.Equals(id, Asm.Id, StringComparison.OrdinalIgnoreCase) ? Asm : Stepper;

    public static FirmwareTargetProfile? FromSerial(string? serialNumber)
    {
        var serial = serialNumber?.Trim().ToUpperInvariant();
        if (serial is not { Length: 9 } || !serial[1..].All(char.IsDigit)) return null;
        return serial[0] switch
        {
            'A' => Asm,
            'S' => Stepper,
            _ => null
        };
    }
}

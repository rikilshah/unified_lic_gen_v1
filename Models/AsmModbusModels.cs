namespace UnifiedLicGen.Models;

public sealed record AsmOutputState(
    ushort Pwm1Duty,
    ushort Pwm2Duty,
    ushort Control,
    ushort LedAddress,
    ushort Red,
    ushort Green,
    ushort Blue,
    ushort Brightness,
    ushort FrequencyHz);

public sealed record AsmLiveState(
    ushort Status,
    ushort Pwm1Applied,
    ushort Pwm2Applied,
    ushort FrequencyAppliedHz,
    ushort Prescaler,
    ushort ClockDivision,
    bool Pedal1Latched,
    bool Pedal2Latched,
    ushort FirmwareMajor,
    ushort FirmwareMinor,
    AsmOutputState Outputs)
{
    public bool BlowerOn => (Status & 0x0001) != 0;
    public bool OnboardLedOn => (Status & 0x0002) != 0;
    public bool Ws2812Busy => (Status & 0x0004) != 0;
    public bool Ip1Active => (Status & 0x0008) != 0;
    public bool Ip2Active => (Status & 0x0010) != 0;
    public bool CommunicationError => (Status & 0x0020) != 0;
}

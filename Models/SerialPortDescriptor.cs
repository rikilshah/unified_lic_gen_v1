namespace UnifiedLicGen.Models;

public sealed record SerialPortDescriptor(string PortName, string FriendlyName)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(FriendlyName)
            ? PortName
            : FriendlyName.Contains(PortName, StringComparison.OrdinalIgnoreCase)
                ? FriendlyName
                : $"{PortName} - {FriendlyName}";

    public override string ToString() => DisplayName;
}


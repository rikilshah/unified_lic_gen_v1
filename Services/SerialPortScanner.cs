using System.IO.Ports;
using Microsoft.Win32;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed class SerialPortScanner
{
    public IReadOnlyList<SerialPortDescriptor> ScanPorts()
    {
        var friendlyNames = ReadFriendlyNamesFromRegistry();

        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetPortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .Select(port => new SerialPortDescriptor(
                port,
                friendlyNames.GetValueOrDefault(port, string.Empty)))
            .ToList();
    }

    private static int GetPortNumber(string portName) =>
        portName.Length > 3 && int.TryParse(portName.AsSpan(3), out var number)
            ? number
            : int.MaxValue;

    private static Dictionary<string, string> ReadFriendlyNamesFromRegistry()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (enumKey is not null)
            {
                CollectFriendlyNames(enumKey, result, depthRemaining: 6);
            }
        }
        catch
        {
            // Device metadata is optional. SerialPort.GetPortNames remains authoritative.
        }

        return result;
    }

    private static void CollectFriendlyNames(
        RegistryKey key,
        Dictionary<string, string> result,
        int depthRemaining)
    {
        TryAddFriendlyName(key, result);
        if (depthRemaining <= 0)
        {
            return;
        }

        string[] children;
        try
        {
            children = key.GetSubKeyNames();
        }
        catch
        {
            return;
        }

        foreach (var childName in children)
        {
            try
            {
                using var child = key.OpenSubKey(childName);
                if (child is not null)
                {
                    CollectFriendlyNames(child, result, depthRemaining - 1);
                }
            }
            catch
            {
                // Protected or transient device keys are skipped.
            }
        }
    }

    private static void TryAddFriendlyName(
        RegistryKey key,
        Dictionary<string, string> result)
    {
        try
        {
            using var parameters = key.OpenSubKey("Device Parameters");
            var portName = parameters?.GetValue("PortName") as string;
            if (string.IsNullOrWhiteSpace(portName) ||
                !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var friendlyName = key.GetValue("FriendlyName") as string;
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyName = key.GetValue("DeviceDesc") as string;
            }

            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                result[portName] = friendlyName;
            }
        }
        catch
        {
            // Raw COM name is still returned when metadata cannot be read.
        }
    }
}


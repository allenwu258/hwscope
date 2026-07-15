using System.Globalization;
using System.Text.RegularExpressions;

namespace HwScope.Core.Hardware.DeviceTopology.Pci;

public sealed record PciAddress(ushort? Segment, byte Bus, byte Device, byte Function)
{
    public override string ToString()
    {
        var coordinate = $"{Bus:X2}:{Device:X2}.{Function:X1}";
        return Segment.HasValue ? $"{Segment.Value:X4}:{coordinate}" : coordinate;
    }
}

internal static partial class PciAddressParser
{
    public static bool TryDecode(uint? busNumber, uint? encodedAddress, out PciAddress? address)
    {
        address = null;
        if (busNumber is null or > byte.MaxValue || encodedAddress is null)
        {
            return false;
        }

        var device = (encodedAddress.Value >> 16) & 0xffff;
        var function = encodedAddress.Value & 0xffff;
        if (device > 31 || function > 7)
        {
            return false;
        }

        address = new PciAddress(null, (byte)busNumber.Value, (byte)device, (byte)function);
        return true;
    }

    public static bool TryParseLastLocationSegment(
        IEnumerable<string> locationPaths,
        out byte device,
        out byte function)
    {
        device = 0;
        function = 0;
        foreach (var path in locationPaths)
        {
            var matches = PciSegmentRegex().Matches(path ?? string.Empty);
            if (matches.Count == 0)
            {
                continue;
            }

            var valueText = matches[^1].Groups[1].Value;
            if (!ushort.TryParse(valueText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var parsedDevice = (value >> 8) & 0xff;
            var parsedFunction = value & 0xff;
            if (parsedDevice > 31 || parsedFunction > 7)
            {
                continue;
            }

            device = (byte)parsedDevice;
            function = (byte)parsedFunction;
            return true;
        }

        return false;
    }

    public static bool TryParseRootIndex(IEnumerable<string> locationPaths, out uint rootIndex)
    {
        rootIndex = 0;
        foreach (var path in locationPaths)
        {
            var match = PciRootRegex().Match(path ?? string.Empty);
            if (match.Success
                && uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rootIndex))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"(?:^|#)PCI\(([0-9A-Fa-f]{4})\)", RegexOptions.CultureInvariant)]
    private static partial Regex PciSegmentRegex();

    [GeneratedRegex(@"(?:^|#)PCIROOT\(([0-9A-Fa-f]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex PciRootRegex();
}

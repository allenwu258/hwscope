using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using HwScope.Core.Windows;

namespace HwScope.Core.Hardware;

public sealed class HardwareCollector
{
    private const string Unknown = "未识别";

    public HardwareReport CollectSummary()
    {
        return new HardwareReport(
            Processor: CollectProcessor(),
            Motherboard: CollectMotherboard(),
            Memory: CollectMemory(),
            Graphics: CollectGraphics(),
            Display: CollectDisplay(),
            Disk: CollectDisk(),
            Audio: CollectAudio(),
            Network: CollectNetwork(),
            GeneratedAt: DateTimeOffset.Now);
    }

    private static string CollectProcessor()
    {
        var cpu = Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
        if (cpu is null)
        {
            return Unknown;
        }

        var name = CleanName(Wmi.GetString(cpu, "Name"));
        var cores = Wmi.GetUInt(cpu, "NumberOfCores");
        var threads = Wmi.GetUInt(cpu, "NumberOfLogicalProcessors");
        var clockMhz = Wmi.GetUInt(cpu, "MaxClockSpeed");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name);
        }

        if (clockMhz > 0 && !ContainsClock(name))
        {
            parts.Add($"@ {clockMhz / 1000.0:0.00}GHz");
        }

        if (cores > 0)
        {
            parts.Add(threads > 0 && threads != cores ? $"{cores}核{threads}线程" : $"{cores}核");
        }

        return JoinOrUnknown(parts);
    }

    private static string CollectMotherboard()
    {
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var manufacturer = CleanName(Wmi.GetString(board, "Manufacturer"));
        var product = CleanName(Wmi.GetString(board, "Product"));
        return JoinOrUnknown(new[] { manufacturer, product }.Where(IsUseful).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string CollectMemory()
    {
        var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory").ToList();
        if (modules.Count == 0)
        {
            return Unknown;
        }

        var totalBytes = modules.Sum(m => (decimal)Wmi.GetULong(m, "Capacity"));
        var total = FormatBinaryBytes(totalBytes, 0);
        var speed = modules.Select(m => Wmi.GetUInt(m, "ConfiguredClockSpeed"))
            .Concat(modules.Select(m => Wmi.GetUInt(m, "Speed")))
            .FirstOrDefault(s => s > 0);
        var ddr = modules.Select(GetMemoryType).FirstOrDefault(IsUseful);
        var layout = string.Join(" + ", modules
            .Select(m => FormatBinaryBytes(Wmi.GetULong(m, "Capacity"), 0))
            .Where(IsUseful));

        var head = string.Join(' ', new[] { total, ddr, speed > 0 ? $"{speed}MHz" : string.Empty }.Where(IsUseful));
        return IsUseful(layout) ? $"{head}（{layout}）" : head;
    }

    private static string CollectGraphics()
    {
        var gpus = Wmi.Query("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController")
            .Select(g => new
            {
                Name = CleanName(Wmi.GetString(g, "Name")),
                Memory = Wmi.GetULong(g, "AdapterRAM"),
                Pnp = Wmi.GetString(g, "PNPDeviceID")
            })
            .Where(g => IsUseful(g.Name))
            .OrderByDescending(g => IsDiscreteGpu(g.Name, g.Pnp))
            .ThenByDescending(g => g.Memory)
            .ToList();

        if (gpus.Count == 0)
        {
            return Unknown;
        }

        return string.Join(" / ", gpus.Select(g =>
        {
            var memory = g.Memory > 0 ? FormatBinaryBytes(g.Memory, 0) : string.Empty;
            return IsUseful(memory) ? $"{g.Name}（{memory}）" : g.Name;
        }));
    }

    private static string CollectDisplay()
    {
        var monitors = Wmi.Query(@"SELECT UserFriendlyName, ManufacturerName, ProductCodeID FROM WmiMonitorID", @"root\wmi")
            .Select(m => new
            {
                Friendly = DecodeUShortArray(m["UserFriendlyName"]),
                Manufacturer = DecodeUShortArray(m["ManufacturerName"]),
                Product = DecodeUShortArray(m["ProductCodeID"])
            })
            .Select(m =>
            {
                var model = IsUseful(m.Friendly) ? m.Friendly : JoinOrUnknown(new[] { m.Manufacturer, m.Product }.Where(IsUseful));
                return CleanName(model);
            })
            .Where(IsUseful)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (monitors.Count > 0)
        {
            return string.Join(" / ", monitors);
        }

        var desktopMonitors = Wmi.Query("SELECT Name FROM Win32_DesktopMonitor")
            .Select(m => CleanName(Wmi.GetString(m, "Name")))
            .Where(IsUseful)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return desktopMonitors.Count > 0 ? string.Join(" / ", desktopMonitors) : Unknown;
    }

    private static string CollectDisk()
    {
        var disks = Wmi.Query("SELECT Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive")
            .Select(d => new
            {
                Model = CleanName(Wmi.GetString(d, "Model")),
                Size = Wmi.GetULong(d, "Size"),
                MediaType = Wmi.GetString(d, "MediaType"),
                Interface = Wmi.GetString(d, "InterfaceType")
            })
            .Where(d => IsUseful(d.Model))
            .OrderByDescending(d => d.Size)
            .ToList();

        if (disks.Count == 0)
        {
            return Unknown;
        }

        return string.Join(" / ", disks.Select(d =>
        {
            var size = d.Size > 0 ? FormatStorageBytes(d.Size, 0) : string.Empty;
            return IsUseful(size) ? $"{d.Model}（{size}）" : d.Model;
        }));
    }

    private static IReadOnlyList<string> CollectAudio()
    {
        var devices = Wmi.Query("SELECT Name FROM Win32_SoundDevice")
            .Select(d => CleanName(Wmi.GetString(d, "Name")))
            .Where(IsUseful)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return devices.Count > 0 ? devices : new[] { Unknown };
    }

    private static string CollectNetwork()
    {
        var adapters = Wmi.Query("SELECT Name, NetConnectionStatus, PhysicalAdapter, AdapterType, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True")
            .Select(a => new
            {
                Name = CleanName(Wmi.GetString(a, "Name")),
                Status = Wmi.GetUInt(a, "NetConnectionStatus"),
                AdapterType = Wmi.GetString(a, "AdapterType"),
                Speed = Wmi.GetULong(a, "Speed")
            })
            .Where(a => IsUseful(a.Name) && !IsVirtualNetworkAdapter(a.Name))
            .OrderByDescending(a => a.Status == 2)
            .ThenByDescending(a => IsWireless(a.Name, a.AdapterType))
            .ThenByDescending(a => a.Speed)
            .ToList();

        if (adapters.Count == 0)
        {
            return Unknown;
        }

        return string.Join(" / ", adapters.Select(a => a.Name));
    }

    private static string GetMemoryType(ManagementObject module)
    {
        var smbiosType = Wmi.GetUInt(module, "SMBIOSMemoryType");
        if (smbiosType is 20 or 21 or 22 or 24 or 26 or 27 or 28 or 29 or 30 or 31 or 34 or 35)
        {
            return smbiosType switch
            {
                20 => "DDR",
                21 => "DDR2",
                22 => "DDR2 FB-DIMM",
                24 => "DDR3",
                26 => "DDR4",
                27 => "LPDDR",
                28 => "LPDDR2",
                29 => "LPDDR3",
                30 => "LPDDR4",
                31 => "逻辑非易失内存",
                34 => "DDR5",
                35 => "LPDDR5",
                _ => string.Empty
            };
        }

        var memoryType = Wmi.GetUInt(module, "MemoryType");
        return memoryType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => string.Empty
        };
    }

    private static bool IsDiscreteGpu(string name, string pnp)
    {
        return name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)
            || pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWireless(string name, string adapterType)
    {
        return name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
            || adapterType.Contains("Wireless", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVirtualNetworkAdapter(string name)
    {
        string[] virtualMarkers = ["Virtual", "Hyper-V", "VMware", "VirtualBox", "Loopback", "TAP", "TUN", "VPN", "Bluetooth"];
        return virtualMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsClock(string value)
    {
        return Regex.IsMatch(value, @"\b\d+(\.\d+)?\s*GHz\b", RegexOptions.IgnoreCase);
    }

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
        return cleaned.Trim('\0');
    }

    private static string DecodeUShortArray(object? value)
    {
        if (value is not ushort[] data)
        {
            return string.Empty;
        }

        var chars = data.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
        return CleanName(new string(chars));
    }

    private static string FormatBinaryBytes(decimal bytes, int decimals)
    {
        return FormatBytes(bytes, decimals, 1024);
    }

    private static string FormatStorageBytes(decimal bytes, int decimals)
    {
        return FormatBytes(bytes, decimals, 1000);
    }

    private static string FormatBytes(decimal bytes, int decimals, int divisor)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = bytes;
        var index = 0;
        while (value >= divisor && index < units.Length - 1)
        {
            value /= divisor;
            index++;
        }

        return $"{Math.Round(value, decimals).ToString($"F{decimals}", CultureInfo.InvariantCulture)}{units[index]}";
    }

    private static string JoinOrUnknown(IEnumerable<string> parts)
    {
        var usefulParts = parts.Where(IsUseful).ToList();
        return usefulParts.Count > 0 ? string.Join(' ', usefulParts) : Unknown;
    }

    private static bool IsUseful(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase);
    }
}


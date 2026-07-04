using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using HwScope.Core.Hardware.Cpu;
using HwScope.Core.Windows;

namespace HwScope.Core.Hardware.Inventory;

public sealed class HardwareInventoryCollector
{
    public HardwareInventorySnapshot Collect()
    {
        var total = Stopwatch.StartNew();
        var steps = new List<HardwareInventoryStepDiagnostic>();

        var processors = CollectStep(steps, "processors", () => Wmi.Query("""
            SELECT Name, Manufacturer, Description, NumberOfCores, NumberOfLogicalProcessors,
                   MaxClockSpeed, CurrentClockSpeed, SocketDesignation, ProcessorId,
                   Architecture, Family, Revision, Stepping
            FROM Win32_Processor
            """).Select(ToProcessor).ToList());

        var baseBoard = CollectStep(steps, "baseboard", () => Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard")
            .Select(ToBaseBoard)
            .FirstOrDefault());

        var bios = CollectStep(steps, "bios", () => Wmi.Query("SELECT SMBIOSBIOSVersion, Version, ReleaseDate FROM Win32_BIOS")
            .Select(ToBios)
            .FirstOrDefault());

        var memoryModules = CollectStep(steps, "memory", () => Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory")
            .Select(ToMemoryModule)
            .ToList());

        var videoControllers = CollectStep(steps, "video", () => Wmi.Query("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController")
            .Select(ToVideoController)
            .ToList());

        var monitors = CollectStep(steps, "monitors", CollectMonitors);

        var diskDrives = CollectStep(steps, "disks", () => Wmi.Query("SELECT Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive")
            .Select(ToDiskDrive)
            .ToList());

        var audioDevices = CollectStep(steps, "audio", () => Wmi.Query("SELECT Name FROM Win32_SoundDevice")
            .Select(ToAudioDevice)
            .ToList());

        var networkAdapters = CollectStep(steps, "network", () => Wmi.Query("SELECT Name, NetConnectionStatus, PhysicalAdapter, AdapterType, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True")
            .Select(ToNetworkAdapter)
            .ToList());

        var cpuTopology = CollectStep(steps, "cpu-topology", CpuTopologyAnalyzer.TryAnalyze);

        total.Stop();
        return new HardwareInventorySnapshot(
            processors ?? [],
            baseBoard,
            bios,
            memoryModules ?? [],
            videoControllers ?? [],
            monitors ?? [],
            diskDrives ?? [],
            audioDevices ?? [],
            networkAdapters ?? [],
            cpuTopology,
            new HardwareInventoryDiagnostics(steps, total.Elapsed),
            DateTimeOffset.Now);
    }

    private static T? CollectStep<T>(ICollection<HardwareInventoryStepDiagnostic> steps, string name, Func<T?> collect)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = collect();
            elapsed.Stop();
            var count = CountItems(result);
            steps.Add(new HardwareInventoryStepDiagnostic(
                name,
                count > 0 ? HardwareInventoryStepStatus.Success : HardwareInventoryStepStatus.Empty,
                count,
                elapsed.Elapsed));
            return result;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            elapsed.Stop();
            steps.Add(new HardwareInventoryStepDiagnostic(name, HardwareInventoryStepStatus.Failed, 0, elapsed.Elapsed, ex.Message));
            return default;
        }
    }

    private static int CountItems<T>(T? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? 0 : 1;
        }

        if (value is System.Collections.ICollection collection)
        {
            return collection.Count;
        }

        return 1;
    }

    private static ProcessorSnapshot ToProcessor(ManagementObject obj)
    {
        return new ProcessorSnapshot(
            Wmi.GetString(obj, "Name"),
            Wmi.GetString(obj, "Manufacturer"),
            Wmi.GetString(obj, "Description"),
            Wmi.GetUInt(obj, "NumberOfCores"),
            Wmi.GetUInt(obj, "NumberOfLogicalProcessors"),
            Wmi.GetUInt(obj, "MaxClockSpeed"),
            Wmi.GetUInt(obj, "CurrentClockSpeed"),
            Wmi.GetString(obj, "SocketDesignation"),
            Wmi.GetString(obj, "ProcessorId"),
            Wmi.GetUInt(obj, "Architecture"),
            Wmi.GetUInt(obj, "Family"),
            Wmi.GetUInt(obj, "Revision"),
            Wmi.GetString(obj, "Stepping"));
    }

    private static BaseBoardSnapshot ToBaseBoard(ManagementObject obj)
    {
        return new BaseBoardSnapshot(Wmi.GetString(obj, "Manufacturer"), Wmi.GetString(obj, "Product"));
    }

    private static BiosSnapshot ToBios(ManagementObject obj)
    {
        return new BiosSnapshot(Wmi.GetString(obj, "SMBIOSBIOSVersion"), Wmi.GetString(obj, "Version"), Wmi.GetString(obj, "ReleaseDate"));
    }

    private static MemoryModuleSnapshot ToMemoryModule(ManagementObject obj)
    {
        return new MemoryModuleSnapshot(
            Wmi.GetULong(obj, "Capacity"),
            Wmi.GetUInt(obj, "Speed"),
            Wmi.GetUInt(obj, "ConfiguredClockSpeed"),
            Wmi.GetUInt(obj, "SMBIOSMemoryType"),
            Wmi.GetUInt(obj, "MemoryType"));
    }

    private static VideoControllerSnapshot ToVideoController(ManagementObject obj)
    {
        return new VideoControllerSnapshot(Wmi.GetString(obj, "Name"), Wmi.GetULong(obj, "AdapterRAM"), Wmi.GetString(obj, "PNPDeviceID"));
    }

    private static IReadOnlyList<MonitorSnapshot> CollectMonitors()
    {
        var monitors = Wmi.Query(@"SELECT UserFriendlyName, ManufacturerName, ProductCodeID FROM WmiMonitorID", @"root\wmi")
            .Select(m => new MonitorSnapshot(
                DecodeUShortArray(m["UserFriendlyName"]),
                DecodeUShortArray(m["ManufacturerName"]),
                DecodeUShortArray(m["ProductCodeID"]),
                string.Empty))
            .ToList();

        if (monitors.Count > 0)
        {
            return monitors;
        }

        return Wmi.Query("SELECT Name FROM Win32_DesktopMonitor")
            .Select(m => new MonitorSnapshot(string.Empty, string.Empty, string.Empty, Wmi.GetString(m, "Name")))
            .ToList();
    }

    private static DiskDriveSnapshot ToDiskDrive(ManagementObject obj)
    {
        return new DiskDriveSnapshot(
            Wmi.GetString(obj, "Model"),
            Wmi.GetULong(obj, "Size"),
            Wmi.GetString(obj, "MediaType"),
            Wmi.GetString(obj, "InterfaceType"));
    }

    private static AudioDeviceSnapshot ToAudioDevice(ManagementObject obj)
    {
        return new AudioDeviceSnapshot(Wmi.GetString(obj, "Name"));
    }

    private static NetworkAdapterSnapshot ToNetworkAdapter(ManagementObject obj)
    {
        return new NetworkAdapterSnapshot(
            Wmi.GetString(obj, "Name"),
            Wmi.GetUInt(obj, "NetConnectionStatus"),
            Wmi.GetString(obj, "PhysicalAdapter").Equals("True", StringComparison.OrdinalIgnoreCase),
            Wmi.GetString(obj, "AdapterType"),
            Wmi.GetULong(obj, "Speed"));
    }

    private static string DecodeUShortArray(object? value)
    {
        if (value is not ushort[] data)
        {
            return string.Empty;
        }

        var chars = data.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
        return new string(chars).Trim();
    }
}

using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using HwScope.Core.Hardware.Cpu;
using HwScope.Core.Windows;

namespace HwScope.Core.Hardware.Inventory;

public sealed class HardwareInventoryCollector
{
    private static readonly string[] CollectionStepNames =
    [
        "processors",
        "baseboard",
        "bios",
        "memory",
        "video",
        "monitors",
        "disks",
        "audio",
        "network",
        "cpu-performance",
        "cpu-topology"
    ];

    public HardwareInventorySnapshot Collect(IProgress<HardwareInventoryCollectionProgress>? progress = null)
    {
        var total = Stopwatch.StartNew();
        var steps = new List<HardwareInventoryStepDiagnostic>();
        var completedSteps = 0;
        var totalSteps = CollectionStepNames.Length;

        var processors = CollectStep(steps, progress, CollectionStepNames[0], totalSteps, ref completedSteps, () => Wmi.Query("""
            SELECT Name, Manufacturer, Description, NumberOfCores, NumberOfLogicalProcessors,
                   MaxClockSpeed, CurrentClockSpeed, SocketDesignation, ProcessorId,
                   Architecture, Family, Revision, Stepping
            FROM Win32_Processor
            """).Select(ToProcessor).ToList());

        var baseBoard = CollectStep(steps, progress, CollectionStepNames[1], totalSteps, ref completedSteps, () => Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard")
            .Select(ToBaseBoard)
            .FirstOrDefault());

        var bios = CollectStep(steps, progress, CollectionStepNames[2], totalSteps, ref completedSteps, () => Wmi.Query("SELECT SMBIOSBIOSVersion, Version, ReleaseDate FROM Win32_BIOS")
            .Select(ToBios)
            .FirstOrDefault());

        var memoryModules = CollectStep(steps, progress, CollectionStepNames[3], totalSteps, ref completedSteps, () => Wmi.Query("SELECT * FROM Win32_PhysicalMemory")
            .Select(ToMemoryModule)
            .ToList());

        var videoControllers = CollectStep(steps, progress, CollectionStepNames[4], totalSteps, ref completedSteps, () => Wmi.Query("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController")
            .Select(ToVideoController)
            .ToList());

        var monitors = CollectStep(steps, progress, CollectionStepNames[5], totalSteps, ref completedSteps, CollectMonitors);

        var diskDrives = CollectStep(steps, progress, CollectionStepNames[6], totalSteps, ref completedSteps, () => Wmi.Query("""
            SELECT Index, DeviceID, PNPDeviceID, Model, FirmwareRevision, SerialNumber,
                   Size, MediaType, InterfaceType, BytesPerSector, Partitions,
                   SCSIBus, SCSIPort, SCSITargetId, SCSILogicalUnit
            FROM Win32_DiskDrive
            """)
            .Select(ToDiskDrive)
            .ToList());

        var audioDevices = CollectStep(steps, progress, CollectionStepNames[7], totalSteps, ref completedSteps, () => Wmi.Query("SELECT Name FROM Win32_SoundDevice")
            .Select(ToAudioDevice)
            .ToList());

        var networkAdapters = CollectStep(steps, progress, CollectionStepNames[8], totalSteps, ref completedSteps, () => Wmi.Query("SELECT Name, NetConnectionStatus, PhysicalAdapter, AdapterType, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True")
            .Select(ToNetworkAdapter)
            .ToList());

        var processorFrequencyMHz = CollectStep(steps, progress, CollectionStepNames[9], totalSteps, ref completedSteps, CollectProcessorFrequencyMHz);
        var cpuTopology = CollectStep(steps, progress, CollectionStepNames[10], totalSteps, ref completedSteps, CpuTopologyAnalyzer.TryAnalyze);

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
            processorFrequencyMHz,
            cpuTopology,
            new HardwareInventoryDiagnostics(steps, total.Elapsed),
            DateTimeOffset.Now);
    }

    private static T? CollectStep<T>(
        ICollection<HardwareInventoryStepDiagnostic> steps,
        IProgress<HardwareInventoryCollectionProgress>? progress,
        string name,
        int totalSteps,
        ref int completedSteps,
        Func<T?> collect)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = collect();
            elapsed.Stop();
            var count = CountItems(result);
            var diagnostic = new HardwareInventoryStepDiagnostic(
                name,
                count > 0 ? HardwareInventoryStepStatus.Success : HardwareInventoryStepStatus.Empty,
                count,
                elapsed.Elapsed);
            steps.Add(diagnostic);
            ReportProgress(progress, diagnostic, totalSteps, ref completedSteps);
            return result;
        }
        catch (Exception ex) when (IsRecoverableCollectionException(ex))
        {
            elapsed.Stop();
            var diagnostic = new HardwareInventoryStepDiagnostic(name, HardwareInventoryStepStatus.Failed, 0, elapsed.Elapsed, ex.Message, ex.ToString());
            steps.Add(diagnostic);
            ReportProgress(progress, diagnostic, totalSteps, ref completedSteps);
            return default;
        }
    }

    private static void ReportProgress(
        IProgress<HardwareInventoryCollectionProgress>? progress,
        HardwareInventoryStepDiagnostic diagnostic,
        int totalSteps,
        ref int completedSteps)
    {
        completedSteps++;
        progress?.Report(new HardwareInventoryCollectionProgress(diagnostic.Name, diagnostic.Status, completedSteps, totalSteps, diagnostic.ItemCount));
    }

    private static bool IsRecoverableCollectionException(Exception ex)
    {
        return ex is ManagementException
            or UnauthorizedAccessException
            or COMException
            or InvalidCastException
            or InvalidOperationException
            or ArgumentException;
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
            Wmi.GetUInt(obj, "MemoryType"),
            Wmi.GetString(obj, "Manufacturer"),
            Wmi.GetString(obj, "PartNumber"),
            Wmi.GetString(obj, "SerialNumber"),
            Wmi.GetString(obj, "BankLabel"),
            Wmi.GetString(obj, "DeviceLocator"),
            Wmi.GetUInt(obj, "FormFactor"),
            Wmi.GetUInt(obj, "DataWidth"),
            Wmi.GetUInt(obj, "TotalWidth"),
            Wmi.GetUInt(obj, "ConfiguredVoltage"),
            Wmi.GetUInt(obj, "MinVoltage"),
            Wmi.GetUInt(obj, "MaxVoltage"),
            Wmi.GetUInt(obj, "MemoryTypeDetail"),
            Wmi.GetUInt(obj, "InterleavePosition"),
            Wmi.GetString(obj, "Tag"));
    }

    private static VideoControllerSnapshot ToVideoController(ManagementObject obj)
    {
        return new VideoControllerSnapshot(Wmi.GetString(obj, "Name"), Wmi.GetULong(obj, "AdapterRAM"), Wmi.GetString(obj, "PNPDeviceID"));
    }

    private static IReadOnlyList<MonitorSnapshot> CollectMonitors()
    {
        var monitors = Wmi.Query(@"SELECT UserFriendlyName, ManufacturerName, ProductCodeID FROM WmiMonitorID", @"root\wmi")
            .Select(m => new MonitorSnapshot(
                DecodeUShortArray(GetRawValue(m, "UserFriendlyName")),
                DecodeUShortArray(GetRawValue(m, "ManufacturerName")),
                DecodeUShortArray(GetRawValue(m, "ProductCodeID")),
                string.Empty))
            .ToList();

        monitors.AddRange(Wmi.Query("SELECT Name FROM Win32_DesktopMonitor")
            .Select(m => new MonitorSnapshot(string.Empty, string.Empty, string.Empty, Wmi.GetString(m, "Name")))
            .Where(m => !monitors.Any(existing => IsSameMonitor(existing, m))));

        return monitors;
    }

    private static bool IsSameMonitor(MonitorSnapshot left, MonitorSnapshot right)
    {
        var leftNames = new[] { left.FriendlyName, left.FallbackName }.Where(value => !string.IsNullOrWhiteSpace(value));
        var rightNames = new[] { right.FriendlyName, right.FallbackName }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return leftNames.Any(leftName => rightNames.Any(rightName => leftName.Equals(rightName, StringComparison.OrdinalIgnoreCase)));
    }

    private static object? GetRawValue(ManagementObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static DiskDriveSnapshot ToDiskDrive(ManagementObject obj)
    {
        return new DiskDriveSnapshot(
            Wmi.GetNullableInt(obj, "Index"),
            Wmi.GetString(obj, "DeviceID"),
            Wmi.GetString(obj, "PNPDeviceID"),
            Wmi.GetString(obj, "Model"),
            Wmi.GetString(obj, "FirmwareRevision"),
            Wmi.GetString(obj, "SerialNumber"),
            Wmi.GetULong(obj, "Size"),
            Wmi.GetString(obj, "MediaType"),
            Wmi.GetString(obj, "InterfaceType"),
            Wmi.GetNullableUInt(obj, "BytesPerSector"),
            Wmi.GetNullableInt(obj, "Partitions"),
            Wmi.GetNullableInt(obj, "SCSIBus"),
            Wmi.GetNullableInt(obj, "SCSIPort"),
            Wmi.GetNullableInt(obj, "SCSITargetId"),
            Wmi.GetNullableInt(obj, "SCSILogicalUnit"));
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

    private static uint CollectProcessorFrequencyMHz()
    {
        var sample = Wmi.Query("""
            SELECT Name, PercentProcessorPerformance, ProcessorFrequency
            FROM Win32_PerfFormattedData_Counters_ProcessorInformation
            WHERE Name = '_Total'
            """).FirstOrDefault();
        return Wmi.GetUInt(sample, "ProcessorFrequency");
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

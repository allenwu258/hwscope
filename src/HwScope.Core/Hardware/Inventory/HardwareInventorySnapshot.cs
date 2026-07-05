using HwScope.Core.Hardware.Cpu;

namespace HwScope.Core.Hardware.Inventory;

public sealed record HardwareInventorySnapshot(
    IReadOnlyList<ProcessorSnapshot> Processors,
    BaseBoardSnapshot? BaseBoard,
    BiosSnapshot? Bios,
    IReadOnlyList<MemoryModuleSnapshot> MemoryModules,
    IReadOnlyList<VideoControllerSnapshot> VideoControllers,
    IReadOnlyList<MonitorSnapshot> Monitors,
    IReadOnlyList<DiskDriveSnapshot> DiskDrives,
    IReadOnlyList<AudioDeviceSnapshot> AudioDevices,
    IReadOnlyList<NetworkAdapterSnapshot> NetworkAdapters,
    uint ProcessorFrequencyMHz,
    CpuTopologyAnalysis? CpuTopology,
    HardwareInventoryDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);

public sealed record ProcessorSnapshot(
    string Name,
    string Manufacturer,
    string Description,
    uint NumberOfCores,
    uint NumberOfLogicalProcessors,
    uint MaxClockSpeed,
    uint CurrentClockSpeed,
    string SocketDesignation,
    string ProcessorId,
    uint Architecture,
    uint Family,
    uint Revision,
    string Stepping);

public sealed record BaseBoardSnapshot(string Manufacturer, string Product);

public sealed record BiosSnapshot(string SmbiosBiosVersion, string Version, string ReleaseDate);

public sealed record MemoryModuleSnapshot(
    ulong Capacity,
    uint Speed,
    uint ConfiguredClockSpeed,
    uint SmbiosMemoryType,
    uint MemoryType);

public sealed record VideoControllerSnapshot(string Name, ulong AdapterRam, string PnpDeviceId);

public sealed record MonitorSnapshot(string FriendlyName, string ManufacturerName, string ProductCodeId, string FallbackName);

public sealed record DiskDriveSnapshot(string Model, ulong Size, string MediaType, string InterfaceType);

public sealed record AudioDeviceSnapshot(string Name);

public sealed record NetworkAdapterSnapshot(
    string Name,
    uint NetConnectionStatus,
    bool PhysicalAdapter,
    string AdapterType,
    ulong Speed);

public sealed record HardwareInventoryDiagnostics(
    IReadOnlyList<HardwareInventoryStepDiagnostic> Steps,
    TimeSpan Elapsed);

public sealed record HardwareInventoryStepDiagnostic(
    string Name,
    HardwareInventoryStepStatus Status,
    int ItemCount,
    TimeSpan Elapsed,
    string? Message = null,
    string? ExceptionText = null);

public sealed record HardwareInventoryCollectionProgress(
    string StepName,
    HardwareInventoryStepStatus Status,
    int CompletedSteps,
    int TotalSteps,
    int ItemCount);

public enum HardwareInventoryStepStatus
{
    Success,
    Empty,
    Failed
}

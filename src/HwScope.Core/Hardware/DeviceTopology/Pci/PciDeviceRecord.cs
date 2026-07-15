namespace HwScope.Core.Hardware.DeviceTopology.Pci;

internal sealed record PciDeviceRecord(
    string InstanceId,
    string? ParentInstanceId,
    string DisplayName,
    string DeviceDescription,
    string Manufacturer,
    Guid? ClassGuid,
    Guid? ContainerId,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    IReadOnlyList<string> LocationPaths,
    string Service,
    uint DevNodeStatus,
    uint? ProblemCode,
    uint? BusNumber,
    uint? DeviceAddress,
    uint? DeviceType,
    uint? BaseClass,
    uint? SubClass,
    uint? ProgrammingInterface,
    uint? CurrentLinkSpeed,
    uint? CurrentLinkWidth,
    uint? MaximumLinkSpeed,
    uint? MaximumLinkWidth,
    uint? CurrentPayloadSize,
    uint? MaximumPayloadSize,
    uint? MaximumReadRequestSize,
    uint? ExpressSpecVersion,
    bool? AerCapabilityPresent,
    uint? InterruptSupport,
    uint? InterruptMessageMaximum,
    uint? BarTypes,
    string DriverDescription,
    string DriverProvider,
    string DriverVersion,
    string DriverInfPath,
    bool IsSyntheticRoot = false,
    uint? RootIndex = null);

internal sealed record PciDeviceSourceResult(
    IReadOnlyList<PciDeviceRecord> Devices,
    IReadOnlyList<DeviceTopologyDiagnostic> Diagnostics);

internal interface IPciDeviceSource
{
    PciDeviceSourceResult ReadPresentDevices();
}

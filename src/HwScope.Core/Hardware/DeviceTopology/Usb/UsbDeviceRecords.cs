namespace HwScope.Core.Hardware.DeviceTopology.Usb;

internal sealed record UsbControllerRecord(
    string DevicePath,
    PnpDeviceIdentity Identity,
    UsbHubRecord? RootHub);

internal sealed record UsbHubRecord(
    string SymbolicName,
    int PortCount,
    bool? IsBusPowered,
    bool IsRoot,
    IReadOnlyList<UsbPortRecord> Ports);

internal sealed record UsbPortRecord(
    int PortNumber,
    UsbConnectionStatus ConnectionStatus,
    UsbConnectionSpeed ConnectionSpeed,
    UsbSupportedProtocols SupportedProtocols,
    bool DeviceIsHub,
    ushort DeviceAddress,
    uint OpenPipeCount,
    UsbDeviceDescriptorInfo? DeviceDescriptor,
    string DriverKey,
    bool IsUserConnectable,
    bool IsDebugCapable,
    bool IsTypeC,
    int? CompanionPortNumber,
    string CompanionHubSymbolicName,
    bool IsDeviceSuperSpeedCapable,
    bool IsDeviceOperatingAtSuperSpeed,
    bool IsDeviceSuperSpeedPlusCapable,
    bool IsDeviceOperatingAtSuperSpeedPlus,
    PnpDeviceIdentity? Identity,
    UsbHubRecord? DownstreamHub);

internal sealed record UsbDeviceSourceResult(
    IReadOnlyList<UsbControllerRecord> Controllers,
    IReadOnlyList<DeviceTopologyDiagnostic> Diagnostics);

internal interface IUsbDeviceSource
{
    UsbDeviceSourceResult ReadPresentTopology();
}

namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public enum UsbTopologyNodeKind
{
    HostController,
    RootHub,
    Port,
    Hub,
    Device,
    Error
}

public enum UsbConnectionStatus
{
    NoDeviceConnected = 0,
    DeviceConnected = 1,
    DeviceFailedEnumeration = 2,
    DeviceGeneralFailure = 3,
    DeviceCausedOvercurrent = 4,
    DeviceNotEnoughPower = 5,
    DeviceNotEnoughBandwidth = 6,
    DeviceHubNestedTooDeeply = 7,
    DeviceInLegacyHub = 8,
    DeviceEnumerating = 9,
    DeviceReset = 10,
    Unknown = -1
}

public enum UsbConnectionSpeed
{
    Low,
    Full,
    High,
    Super,
    SuperPlus,
    Unknown
}

[Flags]
public enum UsbSupportedProtocols
{
    None = 0,
    Usb11 = 1,
    Usb20 = 2,
    Usb30 = 4
}

public sealed record UsbDeviceDescriptorInfo(
    byte Length,
    byte DescriptorType,
    ushort UsbVersionBcd,
    byte DeviceClass,
    byte DeviceSubClass,
    byte DeviceProtocol,
    byte MaximumPacketSize0,
    ushort VendorId,
    ushort ProductId,
    ushort DeviceVersionBcd,
    byte ManufacturerStringIndex,
    byte ProductStringIndex,
    byte SerialNumberStringIndex,
    byte ConfigurationCount)
{
    public string VendorProduct => $"{VendorId:X4}:{ProductId:X4}";

    public string UsbVersion => $"{(UsbVersionBcd >> 8):X}.{(UsbVersionBcd >> 4) & 0x0F:X}{UsbVersionBcd & 0x0F:X}";
}

public sealed record UsbHubInfo(
    string SymbolicName,
    int PortCount,
    bool? IsBusPowered,
    bool IsRoot);

public sealed record UsbPortInfo(
    int PortNumber,
    string PortChain,
    UsbConnectionStatus ConnectionStatus,
    UsbConnectionSpeed ConnectionSpeed,
    UsbSupportedProtocols SupportedProtocols,
    bool? IsDeviceSuperSpeedCapable,
    bool? IsDeviceOperatingAtSuperSpeed,
    bool? IsDeviceSuperSpeedPlusCapable,
    bool? IsDeviceOperatingAtSuperSpeedPlus,
    bool? IsUserConnectable,
    bool? IsDebugCapable,
    bool? IsTypeC,
    int? CompanionPortNumber,
    string CompanionHubSymbolicName,
    ushort DeviceAddress,
    uint OpenPipeCount);

public sealed record UsbTopologyNode(
    string NodeId,
    string? ParentNodeId,
    IReadOnlyList<string> ChildNodeIds,
    UsbTopologyNodeKind Kind,
    string DisplayName,
    PnpDeviceIdentity? Identity,
    string ControllerNodeId,
    string DevicePath,
    string DriverKey,
    UsbHubInfo? Hub,
    UsbPortInfo? Port,
    UsbDeviceDescriptorInfo? DeviceDescriptor,
    string? AttachmentId = null);

public sealed record UsbTopologySnapshot(
    IReadOnlyList<UsbTopologyNode> Nodes,
    IReadOnlyList<string> HostControllerNodeIds,
    DeviceTopologyDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt)
{
    public static UsbTopologySnapshot Empty { get; } = new([], [], DeviceTopologyDiagnostics.Empty, DateTimeOffset.MinValue);
}

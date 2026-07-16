using System.Collections.Immutable;

namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public enum UsbEndpointDirection
{
    Out,
    In
}

public enum UsbEndpointTransferType
{
    Control,
    Isochronous,
    Bulk,
    Interrupt
}

public sealed record UsbRawDescriptorInfo(
    byte DescriptorType,
    byte Length,
    ImmutableArray<byte> Bytes);

public sealed record UsbSuperSpeedEndpointCompanionInfo(
    byte MaximumBurst,
    byte Attributes,
    ushort BytesPerInterval);

public sealed record UsbEndpointDescriptorInfo(
    byte Address,
    UsbEndpointDirection Direction,
    UsbEndpointTransferType TransferType,
    byte SynchronizationType,
    byte UsageType,
    ushort RawMaximumPacketSize,
    int MaximumPacketBytes,
    int TransactionsPerMicroframe,
    byte Interval,
    UsbSuperSpeedEndpointCompanionInfo? SuperSpeedCompanion);

public sealed record UsbInterfaceDescriptorInfo(
    byte InterfaceNumber,
    byte AlternateSetting,
    byte DeclaredEndpointCount,
    byte InterfaceClass,
    byte InterfaceSubClass,
    byte InterfaceProtocol,
    byte DescriptionStringIndex,
    string? Description,
    ImmutableArray<UsbEndpointDescriptorInfo> Endpoints);

public sealed record UsbInterfaceAssociationInfo(
    byte FirstInterface,
    byte InterfaceCount,
    byte FunctionClass,
    byte FunctionSubClass,
    byte FunctionProtocol,
    byte DescriptionStringIndex,
    string? Description);

public sealed record UsbConfigurationDescriptorInfo(
    byte DescriptorIndex,
    ushort TotalLength,
    byte DeclaredInterfaceCount,
    byte ConfigurationValue,
    byte DescriptionStringIndex,
    string? Description,
    byte Attributes,
    bool IsSelfPowered,
    bool SupportsRemoteWakeup,
    int MaximumPowerMilliamps,
    ImmutableArray<UsbInterfaceAssociationInfo> InterfaceAssociations,
    ImmutableArray<UsbInterfaceDescriptorInfo> Interfaces,
    ImmutableArray<UsbRawDescriptorInfo> AdditionalDescriptors,
    ImmutableArray<byte> RawBytes);

public sealed record UsbBosCapabilityInfo(
    byte CapabilityType,
    string DisplayName,
    ImmutableArray<byte> RawBytes);

public sealed record UsbBosDescriptorInfo(
    ushort TotalLength,
    byte DeclaredCapabilityCount,
    ImmutableArray<UsbBosCapabilityInfo> Capabilities,
    ImmutableArray<byte> RawBytes);

public sealed record UsbLanguageInfo(ushort LanguageId, string DisplayName);

public sealed record UsbDeviceDetailSnapshot(
    string AttachmentId,
    string DeviceNodeId,
    string? Manufacturer,
    string? Product,
    string? SerialNumber,
    ImmutableArray<UsbLanguageInfo> Languages,
    ImmutableArray<UsbConfigurationDescriptorInfo> Configurations,
    UsbBosDescriptorInfo? Bos,
    DeviceTopologyDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);

public sealed record UsbDeviceDetailTarget(
    string AttachmentId,
    string DeviceNodeId,
    string ParentHubSymbolicName,
    int PortNumber,
    UsbDeviceDescriptorInfo DeviceDescriptor)
{
    public static UsbDeviceDetailTarget? FromSnapshot(UsbTopologySnapshot snapshot, string nodeId)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        if (!nodes.TryGetValue(nodeId, out var device)
            || device.Kind is not (UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub)
            || device.AttachmentId is null
            || device.DeviceDescriptor is null
            || device.ParentNodeId is null
            || !nodes.TryGetValue(device.ParentNodeId, out var port)
            || port.Port is null
            || port.ParentNodeId is null
            || !nodes.TryGetValue(port.ParentNodeId, out var parentHub)
            || parentHub.Hub is null
            || string.IsNullOrWhiteSpace(parentHub.Hub.SymbolicName))
        {
            return null;
        }

        return new UsbDeviceDetailTarget(
            device.AttachmentId,
            device.NodeId,
            parentHub.Hub.SymbolicName,
            port.Port.PortNumber,
            device.DeviceDescriptor);
    }
}

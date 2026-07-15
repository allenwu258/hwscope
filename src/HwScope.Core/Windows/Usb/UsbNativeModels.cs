namespace HwScope.Core.Windows.Usb;

internal sealed record UsbNativeDeviceDescriptor(
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
    byte ConfigurationCount);

internal sealed record UsbNativeHubInformation(int PortCount, bool IsBusPowered);

internal sealed record UsbNativePortConnection(
    uint ConnectionIndex,
    UsbNativeDeviceDescriptor? DeviceDescriptor,
    byte CurrentConfigurationValue,
    byte Speed,
    bool DeviceIsHub,
    ushort DeviceAddress,
    uint OpenPipeCount,
    int ConnectionStatus);

internal sealed record UsbNativeConnectionV2(
    uint SupportedProtocols,
    uint Flags);

internal sealed record UsbNativeConnectorProperties(
    uint Properties,
    ushort CompanionIndex,
    ushort CompanionPortNumber,
    string CompanionHubSymbolicName);

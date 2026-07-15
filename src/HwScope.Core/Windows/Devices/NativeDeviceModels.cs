namespace HwScope.Core.Windows.Devices;

internal readonly record struct DevicePropertyKey(Guid FormatId, uint PropertyId);

internal sealed record DevicePropertyRequest(string Name, DevicePropertyKey Key);

internal sealed record NativeDeviceProperty(uint Type, byte[] Data);

internal sealed record NativeDeviceInfo(
    string InstanceId,
    IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> Properties);

internal sealed record NativeDeviceDiagnostic(
    string Code,
    string Message,
    string? InstanceId = null,
    string? PropertyName = null);

internal sealed record NativeDeviceEnumerationResult(
    IReadOnlyList<NativeDeviceInfo> Devices,
    IReadOnlyList<NativeDeviceDiagnostic> Diagnostics);

internal sealed record NativeDeviceInterfaceInfo(
    string DevicePath,
    string InstanceId,
    IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> Properties);

internal sealed record NativeDeviceInterfaceEnumerationResult(
    IReadOnlyList<NativeDeviceInterfaceInfo> Interfaces,
    IReadOnlyList<NativeDeviceDiagnostic> Diagnostics);

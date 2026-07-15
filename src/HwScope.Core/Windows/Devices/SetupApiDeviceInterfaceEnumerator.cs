using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HwScope.Core.Windows.Devices;

internal sealed class SetupApiDeviceInterfaceEnumerator
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int MaximumInterfaceDetailBytes = 64 * 1024;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public NativeDeviceInterfaceEnumerationResult EnumeratePresentInterfaces(
        Guid interfaceClassGuid,
        IReadOnlyList<DevicePropertyRequest> requests)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SetupAPI device interface enumeration requires Windows.");
        }

        var classGuid = interfaceClassGuid;
        var deviceInfoSet = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetupDiGetClassDevs failed for interface {interfaceClassGuid:B}.");
        }

        var interfaces = new List<NativeDeviceInterfaceInfo>();
        var diagnostics = new List<NativeDeviceDiagnostic>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref classGuid, index, ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, $"SetupDiEnumDeviceInterfaces failed at index {index}.");
                }

                var deviceInfo = new SetupApiDeviceEnumerator.SpDevinfoData
                {
                    Size = (uint)Marshal.SizeOf<SetupApiDeviceEnumerator.SpDevinfoData>()
                };
                string devicePath;
                try
                {
                    devicePath = ReadDevicePath(deviceInfoSet, ref interfaceData, ref deviceInfo);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
                {
                    diagnostics.Add(new NativeDeviceDiagnostic("setupapi.interface-detail-failed", ex.Message));
                    continue;
                }

                string instanceId;
                try
                {
                    instanceId = SetupApiDeviceEnumerator.ReadInstanceId(deviceInfoSet, ref deviceInfo);
                }
                catch (Win32Exception ex)
                {
                    diagnostics.Add(new NativeDeviceDiagnostic("setupapi.interface-instance-id-failed", ex.Message));
                    continue;
                }

                var properties = new Dictionary<DevicePropertyKey, NativeDeviceProperty>();
                foreach (var request in requests)
                {
                    try
                    {
                        var property = SetupApiDeviceEnumerator.ReadProperty(deviceInfoSet, ref deviceInfo, request.Key);
                        if (property is not null)
                        {
                            properties[request.Key] = property;
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
                    {
                        diagnostics.Add(new NativeDeviceDiagnostic(
                            "setupapi.interface-property-read-failed",
                            ex.Message,
                            instanceId,
                            request.Name));
                    }
                }

                interfaces.Add(new NativeDeviceInterfaceInfo(devicePath, instanceId, properties));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return new NativeDeviceInterfaceEnumerationResult(interfaces, diagnostics);
    }

    private static string ReadDevicePath(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        ref SetupApiDeviceEnumerator.SpDevinfoData deviceInfo)
    {
        SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out var required,
            IntPtr.Zero);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || required < 8 || required > MaximumInterfaceDetailBytes)
        {
            throw new Win32Exception(error, "SetupDiGetDeviceInterfaceDetail size query failed.");
        }

        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(
                    deviceInfoSet,
                    ref interfaceData,
                    detail,
                    required,
                    out _,
                    ref deviceInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed.");
            }

            var path = Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(uint)));
            return string.IsNullOrWhiteSpace(path)
                ? throw new InvalidDataException("SetupAPI returned an empty device interface path.")
                : path;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SetupApiDeviceEnumerator.SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

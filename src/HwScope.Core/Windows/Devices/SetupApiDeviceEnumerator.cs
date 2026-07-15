using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HwScope.Core.Windows.Devices;

internal sealed class SetupApiDeviceEnumerator
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInvalidData = 13;
    private const int ErrorNotFound = 1168;
    private const int MaximumPropertyBytes = 1024 * 1024;
    private const int MaximumInstanceIdChars = 4096;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public NativeDeviceEnumerationResult EnumeratePresentDevices(
        string enumerator,
        IReadOnlyList<DevicePropertyRequest> requests)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SetupAPI device enumeration requires Windows.");
        }

        var deviceInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetupDiGetClassDevs failed for enumerator {enumerator}.");
        }

        var devices = new List<NativeDeviceInfo>();
        var diagnostics = new List<NativeDeviceDiagnostic>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, $"SetupDiEnumDeviceInfo failed at index {index}.");
                }

                string instanceId;
                try
                {
                    instanceId = ReadInstanceId(deviceInfoSet, ref deviceInfo);
                }
                catch (Win32Exception ex)
                {
                    diagnostics.Add(new NativeDeviceDiagnostic(
                        "setupapi.instance-id-failed",
                        ex.Message));
                    continue;
                }

                var properties = new Dictionary<DevicePropertyKey, NativeDeviceProperty>();
                foreach (var request in requests)
                {
                    try
                    {
                        var property = ReadProperty(deviceInfoSet, ref deviceInfo, request.Key);
                        if (property is not null)
                        {
                            properties[request.Key] = property;
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
                    {
                        diagnostics.Add(new NativeDeviceDiagnostic(
                            "setupapi.property-read-failed",
                            ex.Message,
                            instanceId,
                            request.Name));
                    }
                }

                devices.Add(new NativeDeviceInfo(instanceId, properties));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return new NativeDeviceEnumerationResult(devices, diagnostics);
    }

    internal static string ReadInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfo)
    {
        var builder = new StringBuilder(256);
        if (SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref deviceInfo, builder, builder.Capacity, out var required))
        {
            return builder.ToString();
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || required == 0 || required > MaximumInstanceIdChars)
        {
            throw new Win32Exception(error, "SetupDiGetDeviceInstanceId failed.");
        }

        builder.EnsureCapacity((int)required);
        if (!SetupDiGetDeviceInstanceIdW(deviceInfoSet, ref deviceInfo, builder, builder.Capacity, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInstanceId retry failed.");
        }

        return builder.ToString();
    }

    internal static NativeDeviceProperty? ReadProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        DevicePropertyKey key)
    {
        var nativeKey = new Devpropkey { FormatId = key.FormatId, PropertyId = key.PropertyId };
        if (!SetupDiGetDevicePropertyW(
                deviceInfoSet,
                ref deviceInfo,
                ref nativeKey,
                out var propertyType,
                null,
                0,
                out var required,
                0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorNotFound or ErrorInvalidData)
            {
                return null;
            }

            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error, $"SetupDiGetDeviceProperty failed for {key.FormatId:B}/{key.PropertyId}.");
            }
        }

        if (required == 0)
        {
            return new NativeDeviceProperty(propertyType, []);
        }

        if (required > MaximumPropertyBytes)
        {
            throw new InvalidDataException($"Device property requires {required} bytes, exceeding the {MaximumPropertyBytes}-byte limit.");
        }

        var buffer = new byte[required];
        if (!SetupDiGetDevicePropertyW(
                deviceInfoSet,
                ref deviceInfo,
                ref nativeKey,
                out propertyType,
                buffer,
                (uint)buffer.Length,
                out var written,
                0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorNotFound or ErrorInvalidData)
            {
                return null;
            }

            throw new Win32Exception(error, $"SetupDiGetDeviceProperty retry failed for {key.FormatId:B}/{key.PropertyId}.");
        }

        if (written > buffer.Length)
        {
            throw new InvalidDataException($"Device property reported {written} bytes after a {buffer.Length}-byte allocation.");
        }

        return written == buffer.Length
            ? new NativeDeviceProperty(propertyType, buffer)
            : new NativeDeviceProperty(propertyType, buffer.AsSpan(0, (int)written).ToArray());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Devpropkey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        ref Devpropkey propertyKey,
        out uint propertyType,
        [Out] byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

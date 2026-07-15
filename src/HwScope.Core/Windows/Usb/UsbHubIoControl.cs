using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HwScope.Core.Windows.Usb;

internal sealed class UsbHubIoControl : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;
    private const int NameBufferBytes = 16 * 1024;
    private const int ConnectionBufferBytes = 4 * 1024;

    private static readonly uint IoctlGetRootHubName = CtlCode(258);
    private static readonly uint IoctlGetNodeInformation = CtlCode(258);
    private static readonly uint IoctlGetConnectionName = CtlCode(261);
    private static readonly uint IoctlGetConnectionDriverKeyName = CtlCode(264);
    private static readonly uint IoctlGetConnectionInformationEx = CtlCode(274);
    private static readonly uint IoctlGetPortConnectorProperties = CtlCode(278);
    private static readonly uint IoctlGetConnectionInformationExV2 = CtlCode(279);

    private readonly SafeFileHandle _handle;

    private UsbHubIoControl(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static string QueryRootHubName(string controllerDevicePath)
    {
        using var controller = Open(controllerDevicePath);
        var output = controller.IoControl(IoctlGetRootHubName, null, NameBufferBytes, optional: false);
        return UsbNativeBufferParser.ParseVariableLengthName(output, "USB_ROOT_HUB_NAME");
    }

    public static UsbHubIoControl OpenHub(string hubSymbolicName)
    {
        return Open(NormalizeSymbolicPath(hubSymbolicName));
    }

    public UsbNativeHubInformation QueryHubInformation()
    {
        var input = new byte[76];
        var output = IoControl(IoctlGetNodeInformation, input, input.Length, optional: false);
        return UsbNativeBufferParser.ParseHubInformation(output);
    }

    public UsbNativePortConnection QueryConnection(int portNumber)
    {
        var input = CreateConnectionIndexInput(portNumber, ConnectionBufferBytes);
        var output = IoControl(IoctlGetConnectionInformationEx, input, ConnectionBufferBytes, optional: false);
        return UsbNativeBufferParser.ParseConnectionInformation(output);
    }

    public UsbNativeConnectionV2? TryQueryConnectionV2(int portNumber)
    {
        var input = CreateConnectionIndexInput(portNumber, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0x7);
        var output = IoControl(IoctlGetConnectionInformationExV2, input, input.Length, optional: true);
        return output.Length == 0 ? null : UsbNativeBufferParser.ParseConnectionInformationV2(output);
    }

    public UsbNativeConnectorProperties? TryQueryConnectorProperties(int portNumber)
    {
        var input = CreateConnectionIndexInput(portNumber, 16);
        var output = IoControl(IoctlGetPortConnectorProperties, input, NameBufferBytes, optional: true);
        return output.Length == 0 ? null : UsbNativeBufferParser.ParseConnectorProperties(output);
    }

    public string TryQueryConnectionName(int portNumber)
    {
        return TryQueryVariableName(IoctlGetConnectionName, portNumber, "USB_NODE_CONNECTION_NAME");
    }

    public string TryQueryDriverKey(int portNumber)
    {
        return TryQueryVariableName(IoctlGetConnectionDriverKeyName, portNumber, "USB_NODE_CONNECTION_DRIVERKEY_NAME");
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private string TryQueryVariableName(uint ioctl, int portNumber, string structureName)
    {
        var input = CreateConnectionIndexInput(portNumber, 8);
        var output = IoControl(ioctl, input, NameBufferBytes, optional: true);
        return output.Length == 0 ? string.Empty : UsbNativeBufferParser.ParseConnectionVariableLengthName(output, structureName);
    }

    private byte[] IoControl(uint controlCode, byte[]? input, int outputSize, bool optional)
    {
        var output = new byte[outputSize];
        if (!DeviceIoControl(
                _handle,
                controlCode,
                input,
                input?.Length ?? 0,
                output,
                output.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (optional && error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter or ErrorNotFound)
            {
                return [];
            }

            throw new Win32Exception(error, $"USB DeviceIoControl 0x{controlCode:X8} failed.");
        }

        if (bytesReturned < 0 || bytesReturned > output.Length)
        {
            throw new InvalidDataException($"USB DeviceIoControl returned invalid byte count {bytesReturned} for {output.Length}-byte buffer.");
        }

        return output.AsSpan(0, bytesReturned).ToArray();
    }

    private static UsbHubIoControl Open(string devicePath)
    {
        var path = NormalizeSymbolicPath(devicePath);
        var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateFileW(path, GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Unable to open USB device path {path}.");
        }

        return new UsbHubIoControl(handle);
    }

    private static byte[] CreateConnectionIndexInput(int portNumber, int size)
    {
        if (portNumber is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(portNumber));
        }

        var input = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)portNumber);
        return input;
    }

    private static string NormalizeSymbolicPath(string value)
    {
        var path = value.Trim().TrimEnd('\0');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return @"\\?\" + path[4..];
        }

        return @"\\.\" + path.TrimStart('\\');
    }

    private static uint CtlCode(uint function) => (0x22u << 16) | (function << 2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inputBuffer,
        int inputBufferSize,
        [Out] byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

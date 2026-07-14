using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Windows.Storage;

internal sealed record StorageNativeQueryResult(byte[] Buffer, int BytesReturned, StorageError? Error)
{
    public bool Success => Error is null;
}

internal static class StorageDeviceIoControl
{
    public static StorageNativeQueryResult Query(
        string devicePath,
        uint controlCode,
        byte[] input,
        int outputLength,
        uint desiredAccess = 0)
    {
        if (outputLength is <= 0 or > 1_048_576)
        {
            return new StorageNativeQueryResult([], 0, new StorageError(StorageErrorKind.MalformedResponse, "请求的 storage output buffer 大小无效。"));
        }

        using var handle = CreateFileW(
            devicePath,
            desiredAccess,
            StorageNativeConstants.FileShareRead | StorageNativeConstants.FileShareWrite,
            IntPtr.Zero,
            StorageNativeConstants.OpenExisting,
            StorageNativeConstants.FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return new StorageNativeQueryResult([], 0, FromWin32(error, $"无法打开存储设备 {devicePath}。"));
        }

        var output = new byte[outputLength];
        if (!DeviceIoControl(handle, controlCode, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            return new StorageNativeQueryResult([], 0, FromWin32(error, "Windows storage query 失败。"));
        }

        if (returned < 0 || returned > output.Length)
        {
            return new StorageNativeQueryResult([], 0, new StorageError(StorageErrorKind.MalformedResponse, "Storage driver 返回了无效的 byte count。"));
        }

        return new StorageNativeQueryResult(output, returned, null);
    }

    private static StorageError FromWin32(int error, string prefix)
    {
        var kind = error switch
        {
            2 or 3 => StorageErrorKind.DeviceNotFound,
            5 => StorageErrorKind.AccessDenied,
            1 or 50 => StorageErrorKind.ProtocolPassThroughUnavailable,
            21 or 1167 => StorageErrorKind.DeviceRemoved,
            121 or 1460 => StorageErrorKind.Timeout,
            _ => StorageErrorKind.DriverError
        };
        var message = new Win32Exception(error).Message;
        return new StorageError(kind, $"{prefix} {message}", error);
    }

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
        uint controlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

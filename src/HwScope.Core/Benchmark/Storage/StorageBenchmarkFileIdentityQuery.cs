using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace HwScope.Core.Benchmark.Storage;

internal sealed record StorageBenchmarkFileIdentity(ulong VolumeSerialNumber, string FileId);

internal static class StorageBenchmarkFileIdentityQuery
{
    public static StorageBenchmarkFileIdentity Query(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var info,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            throw new IOException($"无法读取测试文件 identity，Win32 error {Marshal.GetLastWin32Error()}。");
        }

        return new StorageBenchmarkFileIdentity(
            info.VolumeSerialNumber,
            $"{info.FileId.High:x16}{info.FileId.Low:x16}");
    }

    internal static bool IsValidFileId(string? value) =>
        value is { Length: 32 } && value.All(character => Uri.IsHexDigit(character));

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);
}

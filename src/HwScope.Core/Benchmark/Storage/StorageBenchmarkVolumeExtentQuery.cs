using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HwScope.Core.Benchmark.Storage;

internal sealed record StorageBenchmarkVolumeExtents(IReadOnlyList<int> DiskNumbers);

internal static class StorageBenchmarkVolumeExtentQuery
{
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public static StorageBenchmarkVolumeExtents? TryQuery(string driveLetter)
    {
        if (driveLetter.Length < 2 || driveLetter[1] != ':')
        {
            return null;
        }

        using var handle = CreateFile(
            $@"\\.\{driveLetter[..2]}",
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[4096];
        if (!DeviceIoControl(
                handle,
                IoctlVolumeGetVolumeDiskExtents,
                IntPtr.Zero,
                0,
                buffer,
                buffer.Length,
                out var returned,
                IntPtr.Zero)
            || returned < sizeof(uint))
        {
            return null;
        }

        var count = BitConverter.ToUInt32(buffer, 0);
        var firstOffset = Marshal.OffsetOf<VolumeDiskExtentsLayout>(nameof(VolumeDiskExtentsLayout.FirstExtent)).ToInt32();
        var extentSize = Marshal.SizeOf<DiskExtentLayout>();
        if (count > 128 || firstOffset + checked((long)count * extentSize) > returned)
        {
            return null;
        }

        var disks = new List<int>(checked((int)count));
        for (var index = 0; index < count; index++)
        {
            var offset = checked(firstOffset + (int)index * extentSize);
            disks.Add(BitConverter.ToInt32(buffer, offset));
        }

        return new StorageBenchmarkVolumeExtents(disks.Distinct().ToList());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeDiskExtentsLayout
    {
        public uint NumberOfDiskExtents;
        public DiskExtentLayout FirstExtent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskExtentLayout
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
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
        IntPtr inputBuffer,
        int inputBufferSize,
        [Out] byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

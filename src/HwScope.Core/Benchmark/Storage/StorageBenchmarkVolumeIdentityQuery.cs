using System.Runtime.InteropServices;
using System.Text;

namespace HwScope.Core.Benchmark.Storage;

internal sealed record StorageBenchmarkVolumeIdentity(string GuidPath, uint SerialNumber);

internal static class StorageBenchmarkVolumeIdentityQuery
{
    public static StorageBenchmarkVolumeIdentity? TryQuery(string rootPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(rootPath));
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var volumeName = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(root, volumeName, volumeName.Capacity))
        {
            return null;
        }

        if (!GetVolumeInformation(
                root,
                null,
                0,
                out var serialNumber,
                out _,
                out _,
                null,
                0))
        {
            return null;
        }

        return new StorageBenchmarkVolumeIdentity(NormalizeGuidPath(volumeName.ToString()), serialNumber);
    }

    internal static string NormalizeGuidPath(string value) => value.Trim().TrimEnd('\\') + "\\";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}

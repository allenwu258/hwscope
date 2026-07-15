using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Benchmark.Storage;

public sealed class StorageBenchmarkTargetDiscovery
{
    public IReadOnlyList<StorageBenchmarkTarget> Discover(IReadOnlyList<StorageDeviceDescriptor>? knownDevices = null)
    {
        var systemDrive = NormalizeDriveLetter(Path.GetPathRoot(Environment.SystemDirectory));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targets = new List<StorageBenchmarkTarget>();
        foreach (var drive in DriveInfo.GetDrives()
                     .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
                     .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var letter = NormalizeDriveLetter(drive.Name);
                var isSystem = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase);
                var extents = StorageBenchmarkVolumeExtentQuery.TryQuery(letter);
                var volumeIdentity = StorageBenchmarkVolumeIdentityQuery.TryQuery(drive.RootDirectory.FullName);
                if (extents is null || extents.DiskNumbers.Count == 0 || volumeIdentity is null)
                {
                    continue;
                }
                var diskNumber = extents.DiskNumbers.Count == 1 ? extents.DiskNumbers[0] : (int?)null;
                var device = diskNumber is { } number
                    ? knownDevices?.FirstOrDefault(candidate => candidate.PhysicalDriveNumber == number)
                    : null;
                var defaultDirectory = isSystem
                    && string.Equals(Path.GetPathRoot(localAppData), drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(localAppData, "HwScope", "StorageBench", "Sessions")
                        : Path.Combine(drive.RootDirectory.FullName, "HwScope-Benchmark");
                targets.Add(new StorageBenchmarkTarget(
                    Id: volumeIdentity.GuidPath.ToUpperInvariant(),
                    RootPath: drive.RootDirectory.FullName,
                    TestDirectory: defaultDirectory,
                    DriveLetter: letter,
                    Label: drive.VolumeLabel,
                    FileSystem: drive.DriveFormat,
                    TotalSizeBytes: drive.TotalSize,
                    FreeSpaceBytes: drive.AvailableFreeSpace,
                    DeviceStableId: device?.StableId ?? string.Empty,
                    PhysicalDriveNumber: diskNumber,
                    Model: device?.Model ?? string.Empty,
                    Bus: device?.InterfaceType ?? drive.DriveType.ToString(),
                    MediaType: device?.MediaType ?? string.Empty,
                    RequiredAlignmentBytes: checked((int)Math.Max(4096u, device?.BytesPerSector ?? 0u)),
                    IsSystem: isSystem,
                    IsRemovable: drive.DriveType == DriveType.Removable,
                    IsMultiExtent: extents.DiskNumbers.Count > 1,
                    VolumeGuidPath: volumeIdentity.GuidPath,
                    VolumeSerialNumber: volumeIdentity.SerialNumber));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return targets;
    }

    private static string NormalizeDriveLetter(string? value)
    {
        var text = value?.Trim().TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        return text.Length >= 2 && text[1] == ':' ? $"{char.ToUpperInvariant(text[0])}:" : string.Empty;
    }
}

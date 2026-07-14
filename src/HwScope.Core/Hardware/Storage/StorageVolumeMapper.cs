using System.Management;
using System.Runtime.InteropServices;
using HwScope.Core.Windows;

namespace HwScope.Core.Hardware.Storage;

internal sealed record StorageVolumeMappingResult(
    IReadOnlyList<StoragePartitionInfo> Partitions,
    IReadOnlyList<StorageVolumeInfo> Volumes,
    IReadOnlyList<StorageDataNote> Notes,
    StorageError? Error = null);

internal sealed class StorageVolumeMapper
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public StorageVolumeMappingResult Collect(int? diskNumber)
    {
        if (!diskNumber.HasValue)
        {
            return new StorageVolumeMappingResult(
                [],
                [],
                [new StorageDataNote("物理磁盘编号不可用，无法建立分区和卷映射。", StorageDataSource.WindowsStorage)]);
        }

        try
        {
            var partitions = QueryMapped($"""
                SELECT DiskNumber, PartitionNumber, DriveLetter, AccessPaths, Offset, Size,
                       GptType, MbrType, IsBoot, IsSystem, IsHidden, IsReadOnly
                FROM MSFT_Partition
                WHERE DiskNumber = {diskNumber.Value}
                """, ToPartition).OrderBy(partition => partition.PartitionNumber).ToList();

            var volumeObjects = QueryMapped("""
                SELECT Path, DriveLetter, FileSystem, FileSystemLabel, Size, SizeRemaining,
                       HealthStatus, OperationalStatus
                FROM MSFT_Volume
                """, ToVolumeSnapshot);

            var volumes = MatchVolumes(partitions, volumeObjects);
            return new StorageVolumeMappingResult(partitions, volumes, []);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            var kind = ex is UnauthorizedAccessException ? StorageErrorKind.AccessDenied : StorageErrorKind.DriverError;
            return new StorageVolumeMappingResult(
                [],
                [],
                [new StorageDataNote("Windows Storage 管理接口未能返回分区和卷映射。", StorageDataSource.WindowsStorage)],
                new StorageError(kind, ex.Message, Diagnostic: ex.ToString()));
        }
    }

    private static IReadOnlyList<T> QueryMapped<T>(string query, Func<ManagementObject, T> map)
    {
        var items = new List<T>();
        using var searcher = new ManagementObjectSearcher(StorageScope, query);
        using var results = searcher.Get();
        foreach (ManagementObject result in results)
        {
            using (result)
            {
                items.Add(map(result));
            }
        }

        return items;
    }

    private static StoragePartitionInfo ToPartition(ManagementObject obj)
    {
        var accessPaths = GetStringArray(obj, "AccessPaths");
        var driveLetter = NormalizeDriveLetter(Wmi.GetString(obj, "DriveLetter"));
        if (driveLetter.Length > 0 && !accessPaths.Contains(driveLetter, StringComparer.OrdinalIgnoreCase))
        {
            accessPaths = [driveLetter, .. accessPaths];
        }

        var gptType = Wmi.GetString(obj, "GptType");
        var mbrType = Wmi.GetNullableInt(obj, "MbrType");
        var style = string.IsNullOrWhiteSpace(gptType) ? mbrType.HasValue ? "MBR" : "Unknown" : "GPT";
        var type = !string.IsNullOrWhiteSpace(gptType) ? gptType : mbrType.HasValue ? $"0x{mbrType.Value:X2}" : string.Empty;

        return new StoragePartitionInfo(
            Wmi.GetNullableInt(obj, "PartitionNumber") ?? 0,
            style,
            type,
            Wmi.GetULong(obj, "Offset"),
            Wmi.GetULong(obj, "Size"),
            accessPaths,
            Wmi.GetNullableBool(obj, "IsBoot") == true,
            Wmi.GetNullableBool(obj, "IsSystem") == true,
            Wmi.GetNullableBool(obj, "IsHidden") == true);
    }

    private static IReadOnlyList<StorageVolumeInfo> MatchVolumes(
        IReadOnlyList<StoragePartitionInfo> partitions,
        IReadOnlyList<WindowsVolumeSnapshot> volumeObjects)
    {
        var matched = new List<StorageVolumeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemDrive = NormalizeDriveLetter(Path.GetPathRoot(Environment.SystemDirectory));

        foreach (var partition in partitions)
        {
            foreach (var volume in volumeObjects)
            {
                var path = NormalizePath(volume.Path);
                var driveLetter = NormalizeDriveLetter(volume.DriveLetter);
                var matches = partition.AccessPaths.Any(accessPath =>
                    string.Equals(NormalizePath(accessPath), path, StringComparison.OrdinalIgnoreCase)
                    || (driveLetter.Length > 0 && string.Equals(NormalizeDriveLetter(accessPath), driveLetter, StringComparison.OrdinalIgnoreCase)));
                if (!matches)
                {
                    continue;
                }

                var key = path.Length > 0 ? path : driveLetter;
                if (!seen.Add(key))
                {
                    continue;
                }

                var roles = new List<string>();
                if (partition.IsSystem || string.Equals(driveLetter, systemDrive, StringComparison.OrdinalIgnoreCase))
                {
                    roles.Add("System");
                }

                if (partition.IsBoot)
                {
                    roles.Add("Boot");
                }

                if (partition.IsHidden)
                {
                    roles.Add("Hidden");
                }

                matched.Add(new StorageVolumeInfo(
                    path,
                    driveLetter,
                    volume.Label,
                    volume.FileSystem,
                    volume.SizeBytes,
                    volume.FreeBytes,
                    FormatHealthStatus(volume.HealthStatus),
                    roles));
            }
        }

        return matched;
    }

    private static WindowsVolumeSnapshot ToVolumeSnapshot(ManagementObject obj)
    {
        return new WindowsVolumeSnapshot(
            Wmi.GetString(obj, "Path"),
            Wmi.GetString(obj, "DriveLetter"),
            Wmi.GetString(obj, "FileSystemLabel"),
            Wmi.GetString(obj, "FileSystem"),
            Wmi.GetULong(obj, "Size"),
            Wmi.GetULong(obj, "SizeRemaining"),
            Wmi.GetNullableInt(obj, "HealthStatus"));
    }

    private static IReadOnlyList<string> GetStringArray(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName] is string[] values
                ? values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (ManagementException)
        {
            return [];
        }
    }

    private static string NormalizeDriveLetter(string? value)
    {
        var text = value?.Trim().TrimEnd('\\') ?? string.Empty;
        if (text.Length == 1 && char.IsLetter(text[0]))
        {
            return $"{char.ToUpperInvariant(text[0])}:";
        }

        return text.Length >= 2 && text[1] == ':' ? $"{char.ToUpperInvariant(text[0])}:" : string.Empty;
    }

    private static string NormalizePath(string? value)
    {
        return value?.Trim().TrimEnd('\\') ?? string.Empty;
    }

    private static string FormatHealthStatus(int? value)
    {
        return value switch
        {
            0 => "Healthy",
            1 => "Warning",
            2 => "Unhealthy",
            5 => "Unknown",
            _ => StorageField.UnknownText
        };
    }

    private sealed record WindowsVolumeSnapshot(
        string Path,
        string DriveLetter,
        string Label,
        string FileSystem,
        ulong SizeBytes,
        ulong FreeBytes,
        int? HealthStatus);
}

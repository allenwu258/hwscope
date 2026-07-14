using HwScope.Core.Hardware.Inventory;

namespace HwScope.Core.Hardware.Storage;

public sealed record StorageDeviceDescriptor(
    string StableId,
    int? PhysicalDriveNumber,
    string DevicePath,
    string PnpDeviceId,
    string Model,
    string FirmwareRevision,
    string SerialNumber,
    ulong CapacityBytes,
    string MediaType,
    string InterfaceType,
    uint? BytesPerSector)
{
    public static StorageDeviceDescriptor FromSnapshot(DiskDriveSnapshot snapshot)
    {
        return new StorageDeviceDescriptor(
            BuildStableId(snapshot),
            snapshot.Index,
            snapshot.DeviceId,
            snapshot.PnpDeviceId,
            Clean(snapshot.Model),
            Clean(snapshot.FirmwareRevision),
            Clean(snapshot.SerialNumber),
            snapshot.Size,
            Clean(snapshot.MediaType),
            Clean(snapshot.InterfaceType),
            snapshot.BytesPerSector);
    }

    private static string BuildStableId(DiskDriveSnapshot snapshot)
    {
        var raw = FirstUseful(
            snapshot.PnpDeviceId,
            $"{snapshot.DeviceId}|{snapshot.SerialNumber}",
            $"disk-{snapshot.Index?.ToString() ?? "unknown"}|{snapshot.Model}|{snapshot.Size}");
        var chars = raw.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var normalized = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? $"disk-{snapshot.Index?.ToString() ?? "unknown"}" : normalized;
    }

    private static string FirstUseful(params string?[] values)
    {
        return values.Select(Clean).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
    }

    private static string Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('\0');
    }
}

public sealed record StorageDeviceIdentity(
    string StableId,
    StorageFieldValue<int> PhysicalDriveNumber,
    StorageFieldValue<string> Model,
    StorageFieldValue<string> Firmware,
    StorageFieldValue<string> SerialNumber,
    StorageFieldValue<string> DevicePath,
    StorageFieldValue<string> DeviceInstanceId,
    StorageFieldValue<ulong> Capacity,
    StorageFieldValue<string> MediaType);

public sealed record StorageInterfaceInfo(
    StorageBusKind Bus,
    StorageProtocolKind Protocol,
    StorageFieldValue<string> BusType,
    StorageFieldValue<string> Standard,
    StorageFieldValue<string> CurrentLink,
    StorageFieldValue<string> MaximumLink,
    StorageFieldValue<string> LogicalSectorSize,
    StorageFieldValue<string> PhysicalSectorSize,
    IReadOnlyList<string> Features);

public sealed record StorageHealthSummary(
    StorageHealthStatus Status,
    string StatusText,
    string StatusReason,
    StorageFieldValue<double> TemperatureCelsius,
    StorageFieldValue<int> RemainingLifePercent,
    IReadOnlyList<string> Flags);

public sealed record StorageLifetimeStatistics(
    StorageFieldValue<string> HostReads,
    StorageFieldValue<string> HostWrites,
    StorageFieldValue<string> PowerCycles,
    StorageFieldValue<string> PowerOnHours,
    StorageFieldValue<string> UnsafeShutdowns,
    StorageFieldValue<string> MediaErrors,
    StorageFieldValue<string> ErrorLogEntries);

public sealed record StorageProtocolAttribute(
    string Id,
    string Name,
    StorageAttributeSeverity Severity,
    string DisplayValue,
    string? Unit,
    string RawValue,
    int? Current,
    int? Worst,
    int? Threshold,
    StorageDataSource Source,
    string? Note = null);

public sealed record StoragePartitionInfo(
    int PartitionNumber,
    string Style,
    string Type,
    ulong OffsetBytes,
    ulong SizeBytes,
    IReadOnlyList<string> AccessPaths,
    bool IsBoot,
    bool IsSystem,
    bool IsHidden);

public sealed record StorageVolumeInfo(
    string Path,
    string DriveLetter,
    string Label,
    string FileSystem,
    ulong SizeBytes,
    ulong FreeBytes,
    string HealthStatus,
    IReadOnlyList<string> Roles);

public sealed record StorageDataNote(string Message, StorageDataSource Source);

public sealed record StorageError(
    StorageErrorKind Kind,
    string Message,
    int? NativeErrorCode = null,
    string? Diagnostic = null);

public sealed record StorageProviderDiagnostic(
    string Provider,
    string Status,
    TimeSpan Elapsed,
    StorageError? Error = null);

public sealed record StorageCollectionDiagnostics(IReadOnlyList<StorageProviderDiagnostic> Providers);

public sealed record StorageDetailReport(
    StorageDeviceIdentity Identity,
    StorageInterfaceInfo Interface,
    StorageHealthSummary Health,
    StorageLifetimeStatistics Lifetime,
    IReadOnlyList<StorageProtocolAttribute> Attributes,
    IReadOnlyList<StoragePartitionInfo> Partitions,
    IReadOnlyList<StorageVolumeInfo> Volumes,
    IReadOnlyList<StorageDataNote> Notes,
    StorageCollectionDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);

internal sealed record StorageProviderData(
    string? Model = null,
    string? Firmware = null,
    string? SerialNumber = null,
    StorageBusKind Bus = StorageBusKind.Unknown,
    StorageProtocolKind Protocol = StorageProtocolKind.Unknown,
    string? Standard = null,
    string? LogicalSectorSize = null,
    string? PhysicalSectorSize = null,
    IReadOnlyList<string>? Features = null,
    StorageHealthSummary? Health = null,
    StorageLifetimeStatistics? Lifetime = null,
    IReadOnlyList<StorageProtocolAttribute>? Attributes = null,
    IReadOnlyList<StorageDataNote>? Notes = null,
    StorageError? Error = null,
    StorageDataSource ModelSource = StorageDataSource.Unknown,
    StorageDataSource FirmwareSource = StorageDataSource.Unknown,
    StorageDataSource SerialNumberSource = StorageDataSource.Unknown,
    StorageDataSource InterfaceSource = StorageDataSource.Unknown);

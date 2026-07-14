using HwScope.Core.Hardware.Storage.Providers;

namespace HwScope.Core.Hardware.Storage;

public sealed class StorageDetailCollector
{
    private readonly StorageVolumeMapper _volumeMapper;
    private readonly IReadOnlyList<IStorageDetailProvider> _providers;

    public StorageDetailCollector()
        : this(new StorageVolumeMapper(), [new WindowsStoragePropertyProvider(), new NvmeStorageProvider(), new AtaSmartStorageProvider()])
    {
    }

    internal StorageDetailCollector(StorageVolumeMapper volumeMapper, IReadOnlyList<IStorageDetailProvider> providers)
    {
        _volumeMapper = volumeMapper;
        _providers = providers;
    }

    public async Task<StorageDetailReport> CollectAsync(
        StorageDeviceDescriptor device,
        CancellationToken cancellationToken = default)
    {
        var volumeMapping = await Task.Run(() => _volumeMapper.Collect(device.PhysicalDriveNumber), cancellationToken).ConfigureAwait(false);
        var current = CreateBaselineData(device);
        var diagnostics = new List<StorageProviderDiagnostic>();
        var notes = volumeMapping.Notes.ToList();
        if (volumeMapping.Error is not null)
        {
            diagnostics.Add(new StorageProviderDiagnostic("Windows Storage", "Failed", TimeSpan.Zero, volumeMapping.Error));
        }

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.CanHandle(device, current))
            {
                continue;
            }

            var result = await provider.QueryAsync(device, current, cancellationToken).ConfigureAwait(false);
            diagnostics.Add(new StorageProviderDiagnostic(result.Name, result.Data.Error is null ? "Success" : "Failed", result.Elapsed, result.Data.Error));
            if (result.Data.Error is not null)
            {
                notes.Add(new StorageDataNote(result.Data.Error.Message, SourceForProvider(result.Name)));
            }

            current = Merge(current, result.Data);
        }

        var health = current.Health ?? new StorageHealthSummary(
            current.Error?.Kind == StorageErrorKind.UnsupportedBus ? StorageHealthStatus.Unsupported : StorageHealthStatus.Unknown,
            current.Error?.Kind == StorageErrorKind.UnsupportedBus ? "不支持" : "未知",
            current.Error?.Message ?? "当前设备未返回原生 SMART / Health 数据。",
            StorageField.Placeholder<double>(),
            StorageField.Placeholder<int>(),
            []);
        var lifetime = current.Lifetime ?? EmptyLifetime();

        return new StorageDetailReport(
            new StorageDeviceIdentity(
                device.StableId,
                StorageField.Number(device.PhysicalDriveNumber, StorageDataSource.Wmi),
                StorageField.Text(FirstUseful(current.Model, device.Model), SourceOr(current.ModelSource, StorageDataSource.Wmi)),
                StorageField.Text(FirstUseful(current.Firmware, device.FirmwareRevision), SourceOr(current.FirmwareSource, StorageDataSource.Wmi)),
                StorageField.Text(FirstUseful(current.SerialNumber, device.SerialNumber), SourceOr(current.SerialNumberSource, StorageDataSource.Wmi)),
                StorageField.Text(device.DevicePath, StorageDataSource.Wmi),
                StorageField.Text(device.PnpDeviceId, StorageDataSource.Wmi),
                StorageField.Bytes(device.CapacityBytes, StorageDataSource.Wmi),
                StorageField.Text(device.MediaType, StorageDataSource.Wmi)),
            new StorageInterfaceInfo(
                current.Bus,
                current.Protocol,
                StorageField.Text(current.Bus != StorageBusKind.Unknown ? StorageDeviceBusProbe.FormatBus(current.Bus) : device.InterfaceType, SourceOr(current.InterfaceSource, StorageDataSource.Wmi)),
                StorageField.Text(current.Standard, SourceOr(current.InterfaceSource, StorageDataSource.StorageApi)),
                StorageField.Placeholder<string>(StorageField.UnknownText),
                StorageField.Placeholder<string>(StorageField.UnknownText),
                StorageField.Text(current.LogicalSectorSize, StorageDataSource.StorageApi),
                StorageField.Text(current.PhysicalSectorSize, StorageDataSource.StorageApi),
                current.Features ?? []),
            health,
            lifetime,
            current.Attributes ?? [],
            volumeMapping.Partitions,
            volumeMapping.Volumes,
            notes.Concat(current.Notes ?? []).ToList(),
            new StorageCollectionDiagnostics(diagnostics),
            DateTimeOffset.Now);
    }

    private static StorageProviderData CreateBaselineData(StorageDeviceDescriptor device)
    {
        return new StorageProviderData(
            Model: device.Model,
            Firmware: device.FirmwareRevision,
            SerialNumber: device.SerialNumber,
            Bus: MapFallbackBus(device.InterfaceType),
            Protocol: StorageProtocolKind.Unknown,
            Features: [],
            ModelSource: StorageDataSource.Wmi,
            FirmwareSource: StorageDataSource.Wmi,
            SerialNumberSource: StorageDataSource.Wmi,
            InterfaceSource: StorageDataSource.Wmi);
    }

    internal static StorageProviderData Merge(StorageProviderData current, StorageProviderData update)
    {
        return new StorageProviderData(
            FirstUseful(update.Model, current.Model),
            FirstUseful(update.Firmware, current.Firmware),
            FirstUseful(update.SerialNumber, current.SerialNumber),
            update.Bus != StorageBusKind.Unknown ? update.Bus : current.Bus,
            update.Protocol != StorageProtocolKind.Unknown ? update.Protocol : current.Protocol,
            FirstUseful(update.Standard, current.Standard),
            FirstUseful(update.LogicalSectorSize, current.LogicalSectorSize),
            FirstUseful(update.PhysicalSectorSize, current.PhysicalSectorSize),
            MergeDistinct(current.Features, update.Features),
            update.Health ?? current.Health,
            update.Lifetime ?? current.Lifetime,
            update.Attributes ?? current.Attributes,
            MergeNotes(current.Notes, update.Notes),
            update.Error ?? current.Error,
            SourceForValue(update.Model, update.ModelSource, current.ModelSource),
            SourceForValue(update.Firmware, update.FirmwareSource, current.FirmwareSource),
            SourceForValue(update.SerialNumber, update.SerialNumberSource, current.SerialNumberSource),
            update.InterfaceSource != StorageDataSource.Unknown ? update.InterfaceSource : current.InterfaceSource);
    }

    private static StorageDataSource SourceForValue(
        string? updateValue,
        StorageDataSource updateSource,
        StorageDataSource currentSource)
    {
        return !string.IsNullOrWhiteSpace(updateValue) && updateSource != StorageDataSource.Unknown
            ? updateSource
            : currentSource;
    }

    private static IReadOnlyList<string> MergeDistinct(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        return (left ?? []).Concat(right ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<StorageDataNote> MergeNotes(IReadOnlyList<StorageDataNote>? left, IReadOnlyList<StorageDataNote>? right)
    {
        return (left ?? []).Concat(right ?? []).DistinctBy(note => note.Message).ToList();
    }

    private static StorageLifetimeStatistics EmptyLifetime()
    {
        return new StorageLifetimeStatistics(
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>());
    }

    private static StorageBusKind MapFallbackBus(string interfaceType)
    {
        return interfaceType.Trim().ToUpperInvariant() switch
        {
            "IDE" => StorageBusKind.Ata,
            "SCSI" => StorageBusKind.Scsi,
            "USB" => StorageBusKind.Usb,
            "1394" => StorageBusKind.Unknown,
            _ => StorageBusKind.Unknown
        };
    }

    private static string FirstUseful(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static StorageDataSource SourceForProvider(string name)
    {
        return name.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? StorageDataSource.Nvme : StorageDataSource.StorageApi;
    }

    private static StorageDataSource SourceOr(StorageDataSource value, StorageDataSource fallback)
    {
        return value == StorageDataSource.Unknown ? fallback : value;
    }

}

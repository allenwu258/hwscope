using System.Diagnostics;
using HwScope.Core.Hardware.Storage.Nvme;
using HwScope.Core.Hardware.Storage.Ata;
using HwScope.Core.Windows.Storage;

namespace HwScope.Core.Hardware.Storage.Providers;

internal sealed record StorageProviderResult(string Name, StorageProviderData Data, TimeSpan Elapsed);

internal interface IStorageDetailProvider
{
    string Name { get; }

    bool CanHandle(StorageDeviceDescriptor device, StorageProviderData current);

    Task<StorageProviderResult> QueryAsync(
        StorageDeviceDescriptor device,
        StorageProviderData current,
        CancellationToken cancellationToken);
}

internal sealed class WindowsStoragePropertyProvider : IStorageDetailProvider
{
    public string Name => "Windows Storage API";

    public bool CanHandle(StorageDeviceDescriptor device, StorageProviderData current)
    {
        return device.PhysicalDriveNumber.HasValue || !string.IsNullOrWhiteSpace(device.DevicePath);
    }

    public async Task<StorageProviderResult> QueryAsync(
        StorageDeviceDescriptor device,
        StorageProviderData current,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var path = ResolvePath(device);
        var data = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var property = StoragePropertyQuery.Query(path);
            if (property.Error is not null)
            {
                return new StorageProviderData(Error: property.Error);
            }

            var features = new List<string>();
            if (property.TrimEnabled == true)
            {
                features.Add("TRIM");
            }

            return new StorageProviderData(
                Model: JoinModel(property.Vendor, property.Product),
                Firmware: property.Revision,
                SerialNumber: property.SerialNumber,
                Bus: property.Bus,
                Protocol: property.Bus == StorageBusKind.Nvme ? StorageProtocolKind.Nvme : property.Bus is StorageBusKind.Ata or StorageBusKind.Sata ? StorageProtocolKind.Ata : StorageProtocolKind.Unknown,
                LogicalSectorSize: property.LogicalSectorSize is > 0 ? $"{property.LogicalSectorSize} B" : null,
                PhysicalSectorSize: property.PhysicalSectorSize is > 0 ? $"{property.PhysicalSectorSize} B" : null,
                Features: features,
                IdentitySource: StorageDataSource.StorageApi,
                InterfaceSource: StorageDataSource.StorageApi);
        }, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        return new StorageProviderResult(Name, data, timer.Elapsed);
    }

    internal static string ResolvePath(StorageDeviceDescriptor device)
    {
        return !string.IsNullOrWhiteSpace(device.DevicePath)
            ? device.DevicePath
            : $@"\\.\PhysicalDrive{device.PhysicalDriveNumber}";
    }

    private static string JoinModel(string vendor, string product)
    {
        var values = new[] { vendor, product }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', values);
    }
}

internal sealed class NvmeStorageProvider : IStorageDetailProvider
{
    public string Name => "NVMe Health";

    public bool CanHandle(StorageDeviceDescriptor device, StorageProviderData current)
    {
        return current.Bus == StorageBusKind.Nvme || current.Protocol == StorageProtocolKind.Nvme;
    }

    public async Task<StorageProviderResult> QueryAsync(
        StorageDeviceDescriptor device,
        StorageProviderData current,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var path = WindowsStoragePropertyProvider.ResolvePath(device);
        var data = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = NvmeProtocolQuery.QueryHealthLog(path);
            if (!query.Success)
            {
                return new StorageProviderData(Bus: StorageBusKind.Nvme, Protocol: StorageProtocolKind.Nvme, Error: query.Error);
            }

            if (!NvmeSmartHealthParser.TryParse(query.Buffer.AsSpan(0, query.BytesReturned), out var log, out var parseError) || log is null)
            {
                return new StorageProviderData(Bus: StorageBusKind.Nvme, Protocol: StorageProtocolKind.Nvme, Error: parseError);
            }

            var evaluation = StorageHealthEvaluator.EvaluateNvme(log);
            return new StorageProviderData(
                Bus: StorageBusKind.Nvme,
                Protocol: StorageProtocolKind.Nvme,
                Standard: "NVM Express",
                Health: evaluation.Health,
                Lifetime: evaluation.Lifetime,
                Attributes: evaluation.Attributes,
                Notes: evaluation.Notes,
                InterfaceSource: StorageDataSource.Nvme);
        }, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        return new StorageProviderResult(Name, data, timer.Elapsed);
    }
}

internal sealed class AtaSmartStorageProvider : IStorageDetailProvider
{
    public string Name => "ATA SMART";

    public bool CanHandle(StorageDeviceDescriptor device, StorageProviderData current)
    {
        return device.PhysicalDriveNumber.HasValue
            && (current.Protocol == StorageProtocolKind.Ata || current.Bus is StorageBusKind.Ata or StorageBusKind.Sata);
    }

    public async Task<StorageProviderResult> QueryAsync(
        StorageDeviceDescriptor device,
        StorageProviderData current,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var path = WindowsStoragePropertyProvider.ResolvePath(device);
        var data = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = AtaSmartQuery.Query(path, device.PhysicalDriveNumber!.Value);
            if (query.Error is not null)
            {
                return new StorageProviderData(Protocol: StorageProtocolKind.Ata, Error: query.Error, InterfaceSource: StorageDataSource.AtaSmart);
            }

            if (!AtaSmartParser.TryParse(query.Attributes, query.Thresholds, out var smart, out var parseError) || smart is null)
            {
                return new StorageProviderData(Protocol: StorageProtocolKind.Ata, Error: parseError, InterfaceSource: StorageDataSource.AtaSmart);
            }

            var evaluation = AtaSmartEvaluator.Evaluate(smart);
            return new StorageProviderData(
                Protocol: StorageProtocolKind.Ata,
                Health: evaluation.Health,
                Lifetime: evaluation.Lifetime,
                Attributes: evaluation.Attributes,
                Notes: evaluation.Notes,
                InterfaceSource: StorageDataSource.AtaSmart);
        }, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        return new StorageProviderResult(Name, data, timer.Elapsed);
    }
}

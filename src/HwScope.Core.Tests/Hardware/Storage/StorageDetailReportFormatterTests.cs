using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class StorageDetailReportFormatterTests
{
    [Fact]
    public void Format_IncludesSourcesRawValuesAndVolumes()
    {
        var report = new StorageDetailReport(
            new StorageDeviceIdentity(
                "disk-0",
                StorageField.Number(0, StorageDataSource.Wmi),
                StorageField.Text("Sample NVMe", StorageDataSource.StorageApi),
                StorageField.Text("1.0", StorageDataSource.StorageApi),
                StorageField.Text("SERIAL", StorageDataSource.StorageApi),
                StorageField.Text(@"\\.\PHYSICALDRIVE0", StorageDataSource.Wmi),
                StorageField.Text("PNP", StorageDataSource.Wmi),
                StorageField.Bytes(1_000_000_000, StorageDataSource.Wmi),
                StorageField.Text("SSD", StorageDataSource.Wmi)),
            new StorageInterfaceInfo(
                StorageBusKind.Nvme,
                StorageProtocolKind.Nvme,
                StorageField.Text("NVMe", StorageDataSource.StorageApi),
                StorageField.Text("NVM Express", StorageDataSource.Nvme),
                StorageField.Placeholder<string>(),
                StorageField.Placeholder<string>(),
                StorageField.Text("512 B", StorageDataSource.StorageApi),
                StorageField.Text("4096 B", StorageDataSource.StorageApi),
                ["TRIM"]),
            new StorageHealthSummary(
                StorageHealthStatus.Good,
                "良好",
                "No warnings.",
                StorageField.Temperature(30, StorageDataSource.Nvme),
                StorageField.Percentage(98, StorageDataSource.Nvme, true),
                []),
            new StorageLifetimeStatistics(
                StorageField.Text("1 TB", StorageDataSource.Nvme),
                StorageField.Text("2 TB", StorageDataSource.Nvme),
                StorageField.Text("3", StorageDataSource.Nvme),
                StorageField.Text("4 小时", StorageDataSource.Nvme),
                StorageField.Text("0", StorageDataSource.Nvme),
                StorageField.Text("0", StorageDataSource.Nvme),
                StorageField.Text("0", StorageDataSource.Nvme)),
            [new StorageProtocolAttribute("02", "温度", StorageAttributeSeverity.Normal, "30", "°C", "0x012F", null, null, null, StorageDataSource.Nvme)],
            [],
            [new StorageVolumeInfo("volume", "C:", "System", "NTFS", 100, 50, "Healthy", ["System"])],
            [],
            new StorageCollectionDiagnostics([]),
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));

        var text = StorageDetailReportFormatter.Format(report);

        Assert.Contains("Model: Sample NVMe [Storage API]", text);
        Assert.Contains("raw 0x012F", text);
        Assert.Contains("C: System NTFS", text);
        Assert.Contains("Generated At: 2026-07-14 00:00:00", text);
    }
}

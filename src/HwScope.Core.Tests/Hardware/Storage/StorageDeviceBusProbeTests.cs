using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class StorageDeviceBusProbeTests
{
    [Theory]
    [InlineData(StorageBusKind.Nvme, "NVMe")]
    [InlineData(StorageBusKind.Sata, "SATA")]
    [InlineData(StorageBusKind.Scsi, "SCSI")]
    [InlineData(StorageBusKind.Usb, "USB")]
    [InlineData(StorageBusKind.Spaces, "Storage Spaces")]
    public void FormatBus_UsesStableDisplayNames(StorageBusKind bus, string expected)
    {
        Assert.Equal(expected, StorageDeviceBusProbe.FormatBus(bus));
    }
}

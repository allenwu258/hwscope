using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class StorageDeviceDescriptorTests
{
    [Fact]
    public void FromSnapshot_PreservesDiskZeroAndUsesPnpId()
    {
        var snapshot = CreateSnapshot(0, "PCI\\VEN_144D&DEV_A80B", "SERIAL");

        var descriptor = StorageDeviceDescriptor.FromSnapshot(snapshot);

        Assert.Equal(0, descriptor.PhysicalDriveNumber);
        Assert.Contains("pci-ven-144d", descriptor.StableId);
    }

    [Fact]
    public void FromSnapshot_UsesDeterministicFallbackWhenIdentityIsMissing()
    {
        var first = StorageDeviceDescriptor.FromSnapshot(CreateSnapshot(null, string.Empty, string.Empty));
        var second = StorageDeviceDescriptor.FromSnapshot(CreateSnapshot(null, string.Empty, string.Empty));

        Assert.Equal(first.StableId, second.StableId);
        Assert.NotEmpty(first.StableId);
    }

    private static DiskDriveSnapshot CreateSnapshot(int? index, string pnpId, string serial)
    {
        return new DiskDriveSnapshot(
            index,
            index.HasValue ? $@"\\.\PHYSICALDRIVE{index.Value}" : string.Empty,
            pnpId,
            "Sample NVMe",
            "1.0",
            serial,
            1_000_000_000,
            "Fixed hard disk media",
            "SCSI",
            512,
            1,
            0,
            0,
            0,
            0);
    }
}

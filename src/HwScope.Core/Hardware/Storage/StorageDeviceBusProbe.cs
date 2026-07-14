using HwScope.Core.Hardware.Storage.Providers;
using HwScope.Core.Windows.Storage;

namespace HwScope.Core.Hardware.Storage;

public static class StorageDeviceBusProbe
{
    public static StorageFieldValue<string> Query(StorageDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var path = WindowsStoragePropertyProvider.ResolvePath(device);
        var property = StoragePropertyQuery.Query(path);
        if (property.Error is null && property.Bus != StorageBusKind.Unknown)
        {
            return StorageField.Text(FormatBus(property.Bus), StorageDataSource.StorageApi);
        }

        return StorageField.Text(
            device.InterfaceType,
            StorageDataSource.Wmi,
            note: property.Error is null ? null : $"Storage descriptor 不可用：{property.Error.Message}");
    }

    internal static string FormatBus(StorageBusKind bus)
    {
        return bus switch
        {
            StorageBusKind.Nvme => "NVMe",
            StorageBusKind.Sata => "SATA",
            StorageBusKind.Ata => "ATA",
            StorageBusKind.Scsi => "SCSI",
            StorageBusKind.Sas => "SAS",
            StorageBusKind.Usb => "USB",
            StorageBusKind.ISCSI => "iSCSI",
            StorageBusKind.Sd => "SD",
            StorageBusKind.Mmc => "MMC",
            StorageBusKind.Ufs => "UFS",
            StorageBusKind.Scm => "SCM",
            StorageBusKind.Spaces => "Storage Spaces",
            _ => bus.ToString()
        };
    }
}

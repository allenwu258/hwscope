using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class StorageProviderMergeTests
{
    [Fact]
    public void Merge_PreservesWmiSourceWhenProviderFieldIsEmpty()
    {
        var baseline = new StorageProviderData(
            Model: "WMI model",
            Firmware: "WMI firmware",
            SerialNumber: "WMI serial",
            ModelSource: StorageDataSource.Wmi,
            FirmwareSource: StorageDataSource.Wmi,
            SerialNumberSource: StorageDataSource.Wmi);
        var update = new StorageProviderData(
            Model: "API model",
            ModelSource: StorageDataSource.StorageApi,
            FirmwareSource: StorageDataSource.StorageApi,
            SerialNumberSource: StorageDataSource.StorageApi);

        var merged = StorageDetailCollector.Merge(baseline, update);

        Assert.Equal(StorageDataSource.StorageApi, merged.ModelSource);
        Assert.Equal(StorageDataSource.Wmi, merged.FirmwareSource);
        Assert.Equal(StorageDataSource.Wmi, merged.SerialNumberSource);
    }

    [Fact]
    public void Merge_UsesProviderSourceForProvidedIdentityFields()
    {
        var baseline = new StorageProviderData(
            Model: "WMI model",
            Firmware: "WMI firmware",
            SerialNumber: "WMI serial",
            ModelSource: StorageDataSource.Wmi,
            FirmwareSource: StorageDataSource.Wmi,
            SerialNumberSource: StorageDataSource.Wmi);
        var update = new StorageProviderData(
            Firmware: "API firmware",
            SerialNumber: "API serial",
            FirmwareSource: StorageDataSource.StorageApi,
            SerialNumberSource: StorageDataSource.StorageApi);

        var merged = StorageDetailCollector.Merge(baseline, update);

        Assert.Equal("WMI model", merged.Model);
        Assert.Equal(StorageDataSource.Wmi, merged.ModelSource);
        Assert.Equal(StorageDataSource.StorageApi, merged.FirmwareSource);
        Assert.Equal(StorageDataSource.StorageApi, merged.SerialNumberSource);
    }
}

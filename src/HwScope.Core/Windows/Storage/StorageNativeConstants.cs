namespace HwScope.Core.Windows.Storage;

internal static class StorageNativeConstants
{
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint SmartGetVersion = 0x00074080;
    public const uint SmartReceiveDriveData = 0x0007C088;

    public const int StorageDeviceProperty = 0;
    public const int StorageAccessAlignmentProperty = 6;
    public const int StorageDeviceTrimProperty = 8;
    public const int StorageAdapterProtocolSpecificProperty = 49;
    public const int StorageDeviceProtocolSpecificProperty = 50;

    public const int PropertyStandardQuery = 0;

    public const int ProtocolTypeNvme = 3;
    public const int NvmeDataTypeIdentify = 1;
    public const int NvmeDataTypeLogPage = 2;
    public const int NvmeSmartHealthLogPage = 0x02;

    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x00000080;
}

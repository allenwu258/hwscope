using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Windows.Storage;

internal sealed record AtaSmartQueryData(byte[] Attributes, byte[] Thresholds, StorageError? Error = null);

internal static class AtaSmartQuery
{
    private const byte SmartCommand = 0xB0;
    private const byte SmartCylinderLow = 0x4F;
    private const byte SmartCylinderHigh = 0xC2;
    private const byte ReadAttributes = 0xD0;
    private const byte ReadThresholds = 0xD1;
    private const uint SmartCapability = 0x00000004;
    private const int OutputHeaderSize = 16;

    public static AtaSmartQueryData Query(string devicePath, int physicalDriveNumber)
    {
        if (physicalDriveNumber is < 0 or > byte.MaxValue)
        {
            return new AtaSmartQueryData([], [], new StorageError(StorageErrorKind.UnsupportedBus, "ATA SMART drive number 超出支持范围。"));
        }

        var access = StorageNativeConstants.GenericRead | StorageNativeConstants.GenericWrite;
        var version = StorageDeviceIoControl.Query(
            devicePath,
            StorageNativeConstants.SmartGetVersion,
            [],
            24,
            StorageNativeConstants.GenericRead);
        if (!version.Success)
        {
            return new AtaSmartQueryData([], [], version.Error);
        }

        if (version.BytesReturned < 8 || (BinaryPrimitives.ReadUInt32LittleEndian(version.Buffer.AsSpan(4, 4)) & SmartCapability) == 0)
        {
            return new AtaSmartQueryData([], [], new StorageError(StorageErrorKind.ProtocolPassThroughUnavailable, "存储驱动未报告 ATA SMART command capability。"));
        }

        var attributes = QuerySector(devicePath, physicalDriveNumber, ReadAttributes, access);
        if (!attributes.Success)
        {
            return new AtaSmartQueryData([], [], attributes.Error);
        }

        var thresholds = QuerySector(devicePath, physicalDriveNumber, ReadThresholds, access);
        if (!thresholds.Success)
        {
            return new AtaSmartQueryData([], [], thresholds.Error);
        }

        return new AtaSmartQueryData(attributes.Buffer, thresholds.Buffer);
    }

    private static StorageNativeQueryResult QuerySector(string devicePath, int driveNumber, byte feature, uint access)
    {
        var input = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0, 4), 512);
        input[4] = feature;
        input[5] = 1;
        input[6] = 1;
        input[7] = SmartCylinderLow;
        input[8] = SmartCylinderHigh;
        input[9] = (byte)(0xA0 | ((driveNumber & 1) << 4));
        input[10] = SmartCommand;
        input[12] = (byte)driveNumber;

        var result = StorageDeviceIoControl.Query(
            devicePath,
            StorageNativeConstants.SmartReceiveDriveData,
            input,
            OutputHeaderSize + 512,
            access);
        if (!result.Success)
        {
            return result;
        }

        if (result.BytesReturned < OutputHeaderSize + 512)
        {
            return new StorageNativeQueryResult([], 0, new StorageError(StorageErrorKind.MalformedResponse, "ATA SMART driver output 长度不足。"));
        }

        if (result.Buffer[4] != 0)
        {
            return new StorageNativeQueryResult([], 0, new StorageError(StorageErrorKind.DriverError, $"ATA SMART driver status error {result.Buffer[4]}。"));
        }

        var payload = result.Buffer.AsSpan(OutputHeaderSize, 512).ToArray();
        return new StorageNativeQueryResult(payload, payload.Length, null);
    }
}

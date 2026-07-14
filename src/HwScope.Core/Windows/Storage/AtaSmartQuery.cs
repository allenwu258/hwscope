using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Windows.Storage;

internal sealed record AtaSmartQueryData(
    byte[] Attributes,
    byte[] Thresholds,
    bool? OverallHealthPassed = null,
    StorageError? OverallStatusError = null,
    StorageError? Error = null);

internal static class AtaSmartQuery
{
    private const byte SmartCommand = 0xB0;
    private const byte SmartCylinderLow = 0x4F;
    private const byte SmartCylinderHigh = 0xC2;
    private const byte ReadAttributes = 0xD0;
    private const byte ReadThresholds = 0xD1;
    private const byte ReturnSmartStatus = 0xDA;
    private const uint SmartCapability = 0x00000004;
    private const int OutputHeaderSize = 16;
    private const ushort AtaFlagsDrdyRequired = 0x0001;

    public static AtaSmartQueryData Query(string devicePath, int physicalDriveNumber)
    {
        if (physicalDriveNumber is < 0 or > byte.MaxValue)
        {
            return new AtaSmartQueryData([], [], Error: new StorageError(StorageErrorKind.UnsupportedBus, "ATA SMART drive number 超出支持范围。"));
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
            return new AtaSmartQueryData([], [], Error: version.Error);
        }

        if (version.BytesReturned < 8 || (BinaryPrimitives.ReadUInt32LittleEndian(version.Buffer.AsSpan(4, 4)) & SmartCapability) == 0)
        {
            return new AtaSmartQueryData([], [], Error: new StorageError(StorageErrorKind.ProtocolPassThroughUnavailable, "存储驱动未报告 ATA SMART command capability。"));
        }

        var attributes = QuerySector(devicePath, physicalDriveNumber, ReadAttributes, access);
        if (!attributes.Success)
        {
            return new AtaSmartQueryData([], [], Error: attributes.Error);
        }

        var thresholds = QuerySector(devicePath, physicalDriveNumber, ReadThresholds, access);
        if (!thresholds.Success)
        {
            return new AtaSmartQueryData([], [], Error: thresholds.Error);
        }

        var overall = QueryOverallStatus(devicePath, physicalDriveNumber);
        return overall.Error is null
            ? new AtaSmartQueryData(attributes.Buffer, thresholds.Buffer, overall.Passed)
            : new AtaSmartQueryData(attributes.Buffer, thresholds.Buffer, OverallStatusError: overall.Error);
    }

    private static AtaOverallStatusResult QueryOverallStatus(string devicePath, int driveNumber)
    {
        var headerSize = IntPtr.Size == 8 ? 48 : 40;
        var currentTaskFileOffset = IntPtr.Size == 8 ? 40 : 32;
        var input = new byte[headerSize];
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(0, 2), (ushort)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(2, 2), AtaFlagsDrdyRequired);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12, 4), 5);

        var taskFile = input.AsSpan(currentTaskFileOffset, 8);
        taskFile[0] = ReturnSmartStatus;
        taskFile[3] = SmartCylinderLow;
        taskFile[4] = SmartCylinderHigh;
        taskFile[5] = (byte)(0xA0 | ((driveNumber & 1) << 4));
        taskFile[6] = SmartCommand;

        var result = StorageDeviceIoControl.Query(
            devicePath,
            StorageNativeConstants.IoctlAtaPassThrough,
            input,
            headerSize,
            StorageNativeConstants.GenericRead | StorageNativeConstants.GenericWrite);
        if (!result.Success)
        {
            return new AtaOverallStatusResult(null, result.Error);
        }

        return TryParseOverallStatus(result.Buffer.AsSpan(0, result.BytesReturned), currentTaskFileOffset, out var passed, out var error)
            ? new AtaOverallStatusResult(passed, null)
            : new AtaOverallStatusResult(null, error);
    }

    internal static bool TryParseOverallStatus(
        ReadOnlySpan<byte> response,
        int currentTaskFileOffset,
        out bool passed,
        out StorageError? error)
    {
        passed = false;
        if (currentTaskFileOffset < 0 || response.Length < currentTaskFileOffset + 8)
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "ATA pass-through status response 长度不足。");
            return false;
        }

        var cylinderLow = response[currentTaskFileOffset + 3];
        var cylinderHigh = response[currentTaskFileOffset + 4];
        if (cylinderLow == SmartCylinderLow && cylinderHigh == SmartCylinderHigh)
        {
            passed = true;
            error = null;
            return true;
        }

        if (cylinderLow == 0xF4 && cylinderHigh == 0x2C)
        {
            error = null;
            return true;
        }

        error = new StorageError(
            StorageErrorKind.MalformedResponse,
            $"ATA SMART RETURN STATUS 返回未知 signature 0x{cylinderHigh:X2}{cylinderLow:X2}。");
        return false;
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

    private sealed record AtaOverallStatusResult(bool? Passed, StorageError? Error);
}

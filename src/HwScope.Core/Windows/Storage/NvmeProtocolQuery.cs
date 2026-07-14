using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Windows.Storage;

internal static class NvmeProtocolQuery
{
    private const int PropertyHeaderSize = 8;
    private const int ProtocolDataSize = 40;
    private const int DescriptorSize = PropertyHeaderSize + ProtocolDataSize;

    public static StorageNativeQueryResult QueryHealthLog(string devicePath)
    {
        var input = new byte[DescriptorSize];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0, 4), StorageNativeConstants.StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4, 4), StorageNativeConstants.PropertyStandardQuery);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(8, 4), StorageNativeConstants.ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(12, 4), StorageNativeConstants.NvmeDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(16, 4), StorageNativeConstants.NvmeSmartHealthLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(24, 4), ProtocolDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(28, 4), 512);

        var result = StorageDeviceIoControl.Query(
            devicePath,
            StorageNativeConstants.IoctlStorageQueryProperty,
            input,
            DescriptorSize + 512);
        if (!result.Success)
        {
            return result;
        }

        return ExtractProtocolPayload(result, 512);
    }

    internal static StorageNativeQueryResult ExtractProtocolPayload(StorageNativeQueryResult result, int minimumLength)
    {
        var span = result.Buffer.AsSpan(0, result.BytesReturned);
        if (span.Length < DescriptorSize)
        {
            return Malformed("STORAGE_PROTOCOL_DATA_DESCRIPTOR 长度不足。");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        if (version < DescriptorSize || size < DescriptorSize || size > span.Length)
        {
            return Malformed("STORAGE_PROTOCOL_DATA_DESCRIPTOR Version/Size 无效。");
        }

        var protocol = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
        var dataType = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12, 4));
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4));
        if (protocol != StorageNativeConstants.ProtocolTypeNvme || dataType != StorageNativeConstants.NvmeDataTypeLogPage)
        {
            return Malformed("Storage driver 返回的 protocol/data type 与 NVMe Health 请求不匹配。");
        }

        if (dataOffset < ProtocolDataSize || dataLength < minimumLength)
        {
            return Malformed("NVMe protocol payload offset/length 无效。");
        }

        var absoluteOffset = checked(PropertyHeaderSize + (long)dataOffset);
        var absoluteEnd = checked(absoluteOffset + dataLength);
        if (absoluteOffset > int.MaxValue || absoluteEnd > span.Length)
        {
            return Malformed("NVMe protocol payload 超出返回 buffer。");
        }

        var payload = span.Slice((int)absoluteOffset, (int)dataLength).ToArray();
        return new StorageNativeQueryResult(payload, payload.Length, null);
    }

    private static StorageNativeQueryResult Malformed(string message)
    {
        return new StorageNativeQueryResult([], 0, new StorageError(StorageErrorKind.MalformedResponse, message));
    }
}

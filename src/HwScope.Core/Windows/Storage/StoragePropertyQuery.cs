using System.Buffers.Binary;
using System.Text;
using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Windows.Storage;

internal sealed record WindowsStoragePropertyData(
    string Vendor,
    string Product,
    string Revision,
    string SerialNumber,
    StorageBusKind Bus,
    uint? LogicalSectorSize,
    uint? PhysicalSectorSize,
    bool? TrimEnabled,
    StorageError? Error = null);

internal static class StoragePropertyQuery
{
    public static WindowsStoragePropertyData Query(string devicePath)
    {
        var descriptorResult = QueryProperty(devicePath, StorageNativeConstants.StorageDeviceProperty, 4096);
        if (!descriptorResult.Success)
        {
            return new WindowsStoragePropertyData(string.Empty, string.Empty, string.Empty, string.Empty, StorageBusKind.Unknown, null, null, null, descriptorResult.Error);
        }

        if (!TryParseDeviceDescriptor(descriptorResult.Buffer.AsSpan(0, descriptorResult.BytesReturned), out var descriptor, out var parseError))
        {
            return new WindowsStoragePropertyData(string.Empty, string.Empty, string.Empty, string.Empty, StorageBusKind.Unknown, null, null, null, parseError);
        }

        uint? logicalSector = null;
        uint? physicalSector = null;
        var alignmentResult = QueryProperty(devicePath, StorageNativeConstants.StorageAccessAlignmentProperty, 128);
        if (alignmentResult.Success && TryParseAlignment(alignmentResult.Buffer.AsSpan(0, alignmentResult.BytesReturned), out var logical, out var physical))
        {
            logicalSector = logical;
            physicalSector = physical;
        }

        bool? trimEnabled = null;
        var trimResult = QueryProperty(devicePath, StorageNativeConstants.StorageDeviceTrimProperty, 64);
        if (trimResult.Success && trimResult.BytesReturned >= 9)
        {
            trimEnabled = trimResult.Buffer[8] != 0;
        }

        return descriptor with { LogicalSectorSize = logicalSector, PhysicalSectorSize = physicalSector, TrimEnabled = trimEnabled };
    }

    internal static bool TryParseDeviceDescriptor(
        ReadOnlySpan<byte> buffer,
        out WindowsStoragePropertyData descriptor,
        out StorageError? error)
    {
        descriptor = new WindowsStoragePropertyData(string.Empty, string.Empty, string.Empty, string.Empty, StorageBusKind.Unknown, null, null, null);
        if (buffer.Length < 36)
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "STORAGE_DEVICE_DESCRIPTOR 长度不足。 ");
            return false;
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        if (declaredSize < 36 || declaredSize > buffer.Length)
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "STORAGE_DEVICE_DESCRIPTOR Size 超出返回 buffer。 ");
            return false;
        }

        var vendorOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));
        var productOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(16, 4));
        var revisionOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(20, 4));
        var serialOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(24, 4));
        var busType = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(28, 4));

        if (!TryReadOffsetString(buffer[..(int)declaredSize], vendorOffset, out var vendor)
            || !TryReadOffsetString(buffer[..(int)declaredSize], productOffset, out var product)
            || !TryReadOffsetString(buffer[..(int)declaredSize], revisionOffset, out var revision)
            || !TryReadOffsetString(buffer[..(int)declaredSize], serialOffset, out var serial))
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "STORAGE_DEVICE_DESCRIPTOR 包含越界字符串 offset。 ");
            return false;
        }

        descriptor = new WindowsStoragePropertyData(vendor, product, revision, serial, MapBus(busType), null, null, null);
        error = null;
        return true;
    }

    private static bool TryParseAlignment(ReadOnlySpan<byte> buffer, out uint logical, out uint physical)
    {
        logical = 0;
        physical = 0;
        if (buffer.Length < 28)
        {
            return false;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        if (size < 28 || size > buffer.Length)
        {
            return false;
        }

        logical = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(16, 4));
        physical = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(20, 4));
        return true;
    }

    private static StorageNativeQueryResult QueryProperty(string devicePath, int propertyId, int outputLength)
    {
        var input = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0, 4), propertyId);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4, 4), StorageNativeConstants.PropertyStandardQuery);
        return StorageDeviceIoControl.Query(devicePath, StorageNativeConstants.IoctlStorageQueryProperty, input, outputLength);
    }

    private static bool TryReadOffsetString(ReadOnlySpan<byte> buffer, uint offset, out string value)
    {
        value = string.Empty;
        if (offset == 0)
        {
            return true;
        }

        if (offset >= buffer.Length)
        {
            return false;
        }

        var tail = buffer[(int)offset..];
        var nul = tail.IndexOf((byte)0);
        if (nul < 0)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(tail[..nul]).Trim().Trim('\0');
        return true;
    }

    private static StorageBusKind MapBus(int value)
    {
        return value switch
        {
            1 => StorageBusKind.Scsi,
            2 or 3 => StorageBusKind.Ata,
            5 => StorageBusKind.Ssa,
            6 => StorageBusKind.Fibre,
            7 => StorageBusKind.Usb,
            8 => StorageBusKind.Raid,
            9 => StorageBusKind.ISCSI,
            10 => StorageBusKind.Sas,
            11 => StorageBusKind.Sata,
            12 => StorageBusKind.Sd,
            13 => StorageBusKind.Mmc,
            14 => StorageBusKind.Virtual,
            15 => StorageBusKind.FileBackedVirtual,
            16 => StorageBusKind.Spaces,
            17 => StorageBusKind.Nvme,
            18 => StorageBusKind.Scm,
            19 => StorageBusKind.Ufs,
            _ => StorageBusKind.Unknown
        };
    }
}

using System.Buffers.Binary;

namespace HwScope.Core.Hardware.Storage.Nvme;

internal static class NvmeSmartHealthParser
{
    public const int RequiredLength = 512;

    public static bool TryParse(ReadOnlySpan<byte> payload, out NvmeSmartHealthLog? log, out StorageError? error)
    {
        log = null;
        if (payload.Length < RequiredLength)
        {
            error = new StorageError(
                StorageErrorKind.MalformedResponse,
                $"NVMe Health payload 长度不足：需要 {RequiredLength} bytes，实际 {payload.Length} bytes。");
            return false;
        }

        var sensors = new List<ushort>(8);
        for (var offset = 200; offset < 216; offset += 2)
        {
            sensors.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2)));
        }

        log = new NvmeSmartHealthLog(
            payload[0],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1, 2)),
            payload[3],
            payload[4],
            payload[5],
            ReadUInt128(payload.Slice(32, 16)),
            ReadUInt128(payload.Slice(48, 16)),
            ReadUInt128(payload.Slice(64, 16)),
            ReadUInt128(payload.Slice(80, 16)),
            ReadUInt128(payload.Slice(96, 16)),
            ReadUInt128(payload.Slice(112, 16)),
            ReadUInt128(payload.Slice(128, 16)),
            ReadUInt128(payload.Slice(144, 16)),
            ReadUInt128(payload.Slice(160, 16)),
            ReadUInt128(payload.Slice(176, 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(192, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(196, 4)),
            sensors,
            payload[..RequiredLength].ToArray());
        error = null;
        return true;
    }

    internal static UInt128 ReadUInt128(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            throw new ArgumentException("A UInt128 value requires 16 bytes.", nameof(bytes));
        }

        var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8));
        return ((UInt128)high << 64) | low;
    }
}

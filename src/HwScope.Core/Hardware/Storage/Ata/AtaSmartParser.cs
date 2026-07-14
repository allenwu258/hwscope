using System.Buffers.Binary;

namespace HwScope.Core.Hardware.Storage.Ata;

internal static class AtaSmartParser
{
    public const int SectorLength = 512;
    private const int EntryOffset = 2;
    private const int EntryLength = 12;
    private const int EntryCount = 30;

    public static bool TryParse(
        ReadOnlySpan<byte> attributeSector,
        ReadOnlySpan<byte> thresholdSector,
        out AtaSmartData? data,
        out StorageError? error)
    {
        data = null;
        if (attributeSector.Length < SectorLength || thresholdSector.Length < SectorLength)
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "ATA SMART attribute/threshold sector 长度不足。");
            return false;
        }

        var attributeRevision = BinaryPrimitives.ReadUInt16LittleEndian(attributeSector[..2]);
        var thresholdRevision = BinaryPrimitives.ReadUInt16LittleEndian(thresholdSector[..2]);
        if (attributeRevision == 0 || thresholdRevision == 0)
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "ATA SMART attribute/threshold revision 无效。");
            return false;
        }

        if (!HasValidChecksum(attributeSector[..SectorLength]) || !HasValidChecksum(thresholdSector[..SectorLength]))
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "ATA SMART attribute/threshold sector checksum 无效。");
            return false;
        }

        var thresholds = new Dictionary<byte, byte>();
        for (var index = 0; index < EntryCount; index++)
        {
            var offset = EntryOffset + index * EntryLength;
            var id = thresholdSector[offset];
            if (id != 0 && !thresholds.ContainsKey(id))
            {
                thresholds[id] = thresholdSector[offset + 1];
            }
        }

        var attributes = new List<AtaSmartAttribute>();
        var seen = new HashSet<byte>();
        for (var index = 0; index < EntryCount; index++)
        {
            var offset = EntryOffset + index * EntryLength;
            var id = attributeSector[offset];
            if (id == 0 || !seen.Add(id))
            {
                continue;
            }

            var raw = attributeSector.Slice(offset + 5, 6).ToArray();
            attributes.Add(new AtaSmartAttribute(
                id,
                BinaryPrimitives.ReadUInt16LittleEndian(attributeSector.Slice(offset + 1, 2)),
                attributeSector[offset + 3],
                attributeSector[offset + 4],
                raw,
                ReadUInt48LittleEndian(raw),
                thresholds.TryGetValue(id, out var threshold) ? threshold : null));
        }

        if (attributes.Count == 0)
        {
            error = new StorageError(StorageErrorKind.MalformedResponse, "ATA SMART attribute sector 未包含有效属性。");
            return false;
        }

        data = new AtaSmartData(
            attributeRevision,
            attributes,
            attributeSector[..SectorLength].ToArray(),
            thresholdSector[..SectorLength].ToArray());
        error = null;
        return true;
    }

    private static bool HasValidChecksum(ReadOnlySpan<byte> sector)
    {
        byte checksum = 0;
        foreach (var value in sector)
        {
            checksum = unchecked((byte)(checksum + value));
        }

        return checksum == 0;
    }

    internal static ulong ReadUInt48LittleEndian(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 6)
        {
            throw new ArgumentException("ATA SMART raw value requires 6 bytes.", nameof(raw));
        }

        ulong value = 0;
        for (var index = 0; index < 6; index++)
        {
            value |= (ulong)raw[index] << (index * 8);
        }
        return value;
    }
}

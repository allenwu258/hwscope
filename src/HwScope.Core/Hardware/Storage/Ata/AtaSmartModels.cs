namespace HwScope.Core.Hardware.Storage.Ata;

internal sealed record AtaSmartAttribute(
    byte Id,
    ushort Flags,
    byte Current,
    byte Worst,
    byte[] RawBytes,
    ulong RawValue,
    byte? Threshold)
{
    public bool IsPreFailure => (Flags & 0x0001) != 0;
}

internal sealed record AtaSmartData(
    ushort Revision,
    IReadOnlyList<AtaSmartAttribute> Attributes,
    byte[] RawAttributeSector,
    byte[] RawThresholdSector);

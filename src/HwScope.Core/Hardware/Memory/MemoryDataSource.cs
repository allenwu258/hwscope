namespace HwScope.Core.Hardware.Memory;

public enum MemoryDataSource
{
    Unknown,
    Wmi,
    Smbios,
    Spd,
    MemoryController,
    Computed,
    Mapping,
    Placeholder
}

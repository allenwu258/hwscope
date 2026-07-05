namespace HwScope.Core.Hardware.Memory;

public static class MemoryTypeFormatter
{
    public static string FormatMemoryType(uint smbiosMemoryType, uint memoryType)
    {
        var type = FormatSmbiosMemoryType(smbiosMemoryType);
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type;
        }

        return memoryType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => string.Empty
        };
    }

    public static string FormatSmbiosMemoryType(uint value)
    {
        return value switch
        {
            20 => "DDR",
            21 => "DDR2",
            22 => "DDR2 FB-DIMM",
            24 => "DDR3",
            26 => "DDR4",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            30 => "LPDDR4",
            31 => "逻辑非易失内存",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => string.Empty
        };
    }

    public static string FormatFormFactor(uint value)
    {
        return value switch
        {
            8 => "DIMM",
            12 => "SO-DIMM",
            13 => "SRIMM",
            14 => "SMD",
            15 => "SSMP",
            16 => "QFP",
            17 => "TQFP",
            18 => "SOIC",
            19 => "LCC",
            20 => "PLCC",
            21 => "BGA",
            22 => "FPBGA",
            23 => "LGA",
            _ => string.Empty
        };
    }
}

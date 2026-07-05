namespace HwScope.Core.Hardware.Memory;

public sealed record SpdProviderResult(
    SpdProviderStatus Status,
    IReadOnlyList<SpdMemoryModule> Modules,
    IReadOnlyList<string> Diagnostics)
{
    public static SpdProviderResult NotConfigured(string message)
    {
        return new SpdProviderResult(SpdProviderStatus.WorkerMissing, [], [message]);
    }

    public bool HasUsableData => Status == SpdProviderStatus.Ok && Modules.Count > 0;
}

public enum SpdProviderStatus
{
    NotConfigured,
    Ok,
    WorkerMissing,
    AccessDenied,
    PlatformBlocked,
    NotImplemented,
    UnsupportedMemoryType,
    ChecksumFailed,
    ParseFailed,
    Timeout,
    Failed
}

public sealed record SpdMemoryModule(
    string Locator,
    string MemoryType,
    string ModuleType,
    ulong CapacityBytes,
    string Manufacturer,
    string DramManufacturer,
    string PartNumber,
    string SerialNumber,
    int ManufacturingWeek,
    int ManufacturingYear,
    string Revision,
    SpdModuleOrganization Organization,
    SpdModuleVoltages Voltages,
    SpdRawInfo Raw,
    IReadOnlyList<SpdTimingProfile> TimingProfiles,
    IReadOnlyList<SpdModuleFeature> Features,
    IReadOnlyList<string> Diagnostics);

public sealed record SpdModuleOrganization(
    int RankCount,
    int BankGroupCount,
    int BanksPerGroup,
    int DeviceWidthBits,
    int BusWidthBits,
    int DataWidthBits,
    int TotalWidthBits);

public sealed record SpdModuleVoltages(
    uint VddMv,
    uint VddqMv,
    uint VppMv);

public sealed record SpdRawInfo(
    int ByteCount,
    bool? ChecksumOk,
    bool? CrcOk,
    string Sha256);

public sealed record SpdModuleFeature(string Name, string Value);

public sealed record SpdTimingProfile(
    string Name,
    string Kind,
    double FrequencyMHz,
    uint EffectiveRateMTps,
    string CasLatency,
    string Trcd,
    string Trp,
    string Tras,
    string Trc,
    uint VoltageMv);

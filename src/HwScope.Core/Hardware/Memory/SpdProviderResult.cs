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
    IReadOnlyList<SpdTimingProfile> TimingProfiles);

public sealed record SpdTimingProfile(
    string Name,
    double FrequencyMHz,
    uint EffectiveRateMTps,
    string CasLatency,
    string Trcd,
    string Trp,
    string Tras,
    string Trc,
    uint VoltageMv);

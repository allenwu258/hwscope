namespace HwScope.Core.Hardware.Cpu;

public sealed record CpuDetailReport(
    CpuIdentity Identity,
    CpuSpecification Specification,
    CpuTopology Topology,
    CpuClockInfo Clocks,
    IReadOnlyList<CpuCacheInfo> Caches,
    IReadOnlyList<CpuCoreMappingInfo> CoreMappings,
    CpuTopologyInspectReport? TopologyInspect,
    IReadOnlyList<CpuFeature> Features,
    CpuPlatformContext Platform,
    IReadOnlyList<CpuDataNote> Notes,
    DateTimeOffset GeneratedAt);

public sealed record CpuIdentity(
    CpuFieldValue<string> DisplayName,
    CpuFieldValue<string> SpecificationName,
    CpuFieldValue<string> Vendor,
    CpuFieldValue<string> CodeName);

public sealed record CpuSpecification(
    CpuFieldValue<string> Package,
    CpuFieldValue<string> Technology,
    CpuFieldValue<string> Tdp,
    CpuFieldValue<string> CoreVoltage,
    CpuFieldValue<string> Family,
    CpuFieldValue<string> Model,
    CpuFieldValue<string> Stepping,
    CpuFieldValue<string> ExtendedFamily,
    CpuFieldValue<string> ExtendedModel,
    CpuFieldValue<string> Revision);

public sealed record CpuTopology(
    CpuFieldValue<int> PackageCount,
    CpuFieldValue<int> CoreCount,
    CpuFieldValue<int> LogicalProcessorCount,
    CpuFieldValue<bool> SmtEnabled,
    CpuFieldValue<int> CpuGroupCount,
    CpuFieldValue<int> NumaNodeCount);

public sealed record CpuClockInfo(
    CpuFieldValue<double> CurrentMHz,
    CpuFieldValue<double> BaseMHz,
    CpuFieldValue<double> MaxMHz,
    CpuFieldValue<double> BusMHz,
    CpuFieldValue<double> Multiplier);

public sealed record CpuCacheInfo(
    CpuCacheLevel Level,
    string Name,
    int? InstanceCount,
    long? SizeBytes,
    int? Ways,
    int? LineSizeBytes,
    int? SharedLogicalProcessorCount,
    string? CacheType,
    IReadOnlyList<CpuProcessorMaskView> SharedMasks,
    CpuDataSource Source,
    bool IsEstimated = false,
    string? Note = null);

public sealed record CpuCoreMappingInfo(
    int CoreIndex,
    bool HasSmt,
    int EfficiencyClass,
    IReadOnlyList<CpuProcessorMaskView> LogicalProcessors,
    CpuDataSource Source);

public sealed record CpuProcessorMaskView(
    ushort Group,
    string ProcessorRange,
    string HexMask,
    int Count)
{
    public string DisplayText => $"group {Group} [{ProcessorRange}] mask={HexMask}";
}

public sealed record CpuFeature(
    string Name,
    CpuFeatureGroup Group,
    bool IsSupported,
    CpuDataSource Source,
    bool IsEstimated = false);

public sealed record CpuPlatformContext(
    CpuFieldValue<string> Motherboard,
    CpuFieldValue<string> BiosVersion,
    CpuFieldValue<string> Chipset,
    CpuFieldValue<string> IntegratedVideo,
    CpuFieldValue<string> MemoryType,
    CpuFieldValue<string> MemoryClock,
    CpuFieldValue<string> DramFsbRatio);

public sealed record CpuDataNote(string Message, CpuDataSource Source);

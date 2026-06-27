namespace HwScope.Core.Hardware.Cpu;

public sealed record CpuTopologyInspectReport(
    IReadOnlyList<CpuTopologyGroupInfo> Groups,
    IReadOnlyList<CpuTopologyPackageInfo> Packages,
    IReadOnlyList<CpuTopologyNumaNodeInfo> NumaNodes,
    IReadOnlyList<CpuCoreMappingInfo> Cores,
    IReadOnlyList<CpuCacheInstanceInfo> CacheInstances,
    IReadOnlyList<CpuTopologyInsight> Insights,
    IReadOnlyList<string> Notes,
    DateTimeOffset GeneratedAt);

public sealed record CpuTopologyGroupInfo(
    ushort Group,
    int MaximumProcessorCount,
    int ActiveProcessorCount,
    CpuProcessorMaskView ActiveMask);

public sealed record CpuTopologyPackageInfo(
    int PackageIndex,
    IReadOnlyList<CpuProcessorMaskView> LogicalProcessors);

public sealed record CpuTopologyNumaNodeInfo(
    uint NodeNumber,
    CpuProcessorMaskView LogicalProcessors);

public sealed record CpuCacheInstanceInfo(
    int CacheIndex,
    int Level,
    string CacheType,
    long SizeBytes,
    int LineSizeBytes,
    int Associativity,
    CpuProcessorMaskView LogicalProcessors);

public sealed record CpuTopologyInsight(
    string Title,
    string Detail,
    CpuTopologyInsightKind Kind);

public enum CpuTopologyInsightKind
{
    Information,
    Heuristic,
    Warning
}

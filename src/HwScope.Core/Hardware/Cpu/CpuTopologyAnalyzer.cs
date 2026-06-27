using HwScope.Core.Windows;

namespace HwScope.Core.Hardware.Cpu;

internal sealed record CpuTopologyAnalysis(
    CpuTopology Topology,
    IReadOnlyList<CpuCacheInfo> Caches,
    IReadOnlyList<CpuCoreMappingInfo> CoreMappings,
    IReadOnlyList<CpuDataNote> Notes);

internal static class CpuTopologyAnalyzer
{
    public static CpuTopologyAnalysis? TryAnalyze()
    {
        var topology = LogicalProcessorInformation.TryCollect();
        if (topology is null)
        {
            return null;
        }

        var coreMappings = topology.Cores
            .Select(core => new CpuCoreMappingInfo(
                core.Index,
                core.HasSmt,
                core.EfficiencyClass,
                core.Masks.Select(ToMaskView).ToList(),
                CpuDataSource.WindowsApi))
            .ToList();

        var caches = topology.Caches
            .GroupBy(cache => new
            {
                cache.Level,
                cache.Type,
                cache.SizeBytes,
                cache.LineSizeBytes,
                cache.Associativity,
                SharedLogicalProcessorCount = cache.Mask.Count
            })
            .OrderBy(group => group.Key.Level)
            .ThenBy(group => CacheTypeOrder(group.Key.Type))
            .Select(group => ToCpuCacheInfo(group.Key.Level, group.Key.Type, group.Count(), group.Key.SizeBytes, group.Key.LineSizeBytes, group.Key.Associativity, group.Key.SharedLogicalProcessorCount, group.Select(cache => ToMaskView(cache.Mask)).ToList()))
            .ToList();

        var smtEnabled = topology.Cores.Any(core => core.HasSmt || core.Masks.Sum(mask => mask.Count) > 1);
        var notes = new List<CpuDataNote>
        {
            new("拓扑、核心映射和缓存信息来自 Windows GetLogicalProcessorInformationEx。", CpuDataSource.WindowsApi)
        };

        return new CpuTopologyAnalysis(
            new CpuTopology(
                PackageCount: CpuField.Number(topology.PackageCount, CpuDataSource.WindowsApi),
                CoreCount: CpuField.Number(topology.PhysicalCoreCount, CpuDataSource.WindowsApi),
                LogicalProcessorCount: CpuField.Number(topology.LogicalProcessorCount, CpuDataSource.WindowsApi),
                SmtEnabled: CpuField.Boolean(smtEnabled, CpuDataSource.WindowsApi),
                CpuGroupCount: CpuField.Number(topology.ActiveGroupCount, CpuDataSource.WindowsApi),
                NumaNodeCount: CpuField.Number(topology.NumaNodeCount, CpuDataSource.WindowsApi)),
            caches,
            coreMappings,
            notes);
    }

    private static CpuCacheInfo ToCpuCacheInfo(
        byte level,
        LogicalCacheType type,
        int instanceCount,
        long sizeBytes,
        int lineSizeBytes,
        int associativity,
        int sharedLogicalProcessorCount,
        IReadOnlyList<CpuProcessorMaskView> masks)
    {
        var cacheLevel = ToCpuCacheLevel(level, type);
        var cacheType = FormatCacheType(type);
        var name = level == 1 && type is LogicalCacheType.Data or LogicalCacheType.Instruction
            ? $"L{level} {cacheType}"
            : $"L{level} {cacheType}";

        return new CpuCacheInfo(
            cacheLevel,
            name,
            instanceCount > 1 ? instanceCount : null,
            sizeBytes,
            associativity > 0 ? associativity : null,
            lineSizeBytes > 0 ? lineSizeBytes : null,
            sharedLogicalProcessorCount > 0 ? sharedLogicalProcessorCount : null,
            cacheType,
            masks,
            CpuDataSource.WindowsApi);
    }

    private static CpuCacheLevel ToCpuCacheLevel(byte level, LogicalCacheType type)
    {
        return level switch
        {
            1 when type == LogicalCacheType.Data => CpuCacheLevel.L1Data,
            1 when type == LogicalCacheType.Instruction => CpuCacheLevel.L1Instruction,
            2 => CpuCacheLevel.L2,
            3 => CpuCacheLevel.L3,
            4 => CpuCacheLevel.L4,
            _ => CpuCacheLevel.L4
        };
    }

    private static string FormatCacheType(LogicalCacheType type)
    {
        return type switch
        {
            LogicalCacheType.Unified => "Unified",
            LogicalCacheType.Instruction => "Instruction",
            LogicalCacheType.Data => "Data",
            LogicalCacheType.Trace => "Trace",
            _ => "Unknown"
        };
    }

    private static CpuProcessorMaskView ToMaskView(LogicalProcessorMask mask)
    {
        return new CpuProcessorMaskView(mask.Group, mask.ProcessorRange, mask.HexMask, mask.Count);
    }

    private static int CacheTypeOrder(LogicalCacheType type)
    {
        return type switch
        {
            LogicalCacheType.Data => 0,
            LogicalCacheType.Instruction => 1,
            LogicalCacheType.Unified => 2,
            LogicalCacheType.Trace => 3,
            _ => 4
        };
    }
}

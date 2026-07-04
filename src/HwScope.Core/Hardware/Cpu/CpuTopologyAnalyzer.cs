using HwScope.Core.Windows;

namespace HwScope.Core.Hardware.Cpu;

public sealed record CpuTopologyAnalysis(
    CpuTopology Topology,
    IReadOnlyList<CpuCacheInfo> Caches,
    IReadOnlyList<CpuCoreMappingInfo> CoreMappings,
    CpuTopologyInspectReport InspectReport,
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

        var cacheInstances = topology.Caches
            .Select((cache, index) => new CpuCacheInstanceInfo(
                index,
                cache.Level,
                FormatCacheType(cache.Type),
                cache.SizeBytes,
                cache.LineSizeBytes,
                cache.Associativity,
                ToMaskView(cache.Mask)))
            .ToList();

        var inspectReport = new CpuTopologyInspectReport(
            topology.Groups.Select(group => new CpuTopologyGroupInfo(group.Group, group.MaximumProcessorCount, group.ActiveProcessorCount, ToMaskView(group.ActiveMask))).ToList(),
            topology.Packages.Select(package => new CpuTopologyPackageInfo(package.Index, package.Masks.Select(ToMaskView).ToList())).ToList(),
            topology.NumaNodes.Select(node => new CpuTopologyNumaNodeInfo(node.NodeNumber, ToMaskView(node.Mask))).ToList(),
            coreMappings,
            cacheInstances,
            BuildInsights(coreMappings, cacheInstances),
            [
                "Topology and cache data are from Windows GetLogicalProcessorInformationEx.",
                "CCD and hybrid-core hints are heuristic. Validate with Ryzen Master, Intel tools, or HWiNFO before using the mapping for affinity rules."
            ],
            DateTimeOffset.Now);

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
            inspectReport,
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

    private static IReadOnlyList<CpuTopologyInsight> BuildInsights(IReadOnlyList<CpuCoreMappingInfo> cores, IReadOnlyList<CpuCacheInstanceInfo> caches)
    {
        var insights = new List<CpuTopologyInsight>();
        AddL3Insights(insights, caches);
        AddHybridInsights(insights, cores, caches);
        return insights;
    }

    private static void AddL3Insights(List<CpuTopologyInsight> insights, IReadOnlyList<CpuCacheInstanceInfo> caches)
    {
        var l3Caches = caches
            .Where(cache => cache.Level == 3)
            .OrderByDescending(cache => cache.SizeBytes)
            .ThenBy(cache => cache.LogicalProcessors.Group)
            .ThenBy(cache => cache.LogicalProcessors.ProcessorRange)
            .ToList();

        if (l3Caches.Count == 0)
        {
            insights.Add(new CpuTopologyInsight("L3 group", "No L3 cache information returned by Windows topology API.", CpuTopologyInsightKind.Information));
            return;
        }

        for (var i = 0; i < l3Caches.Count; i++)
        {
            var cache = l3Caches[i];
            var hint = cache.SizeBytes >= 64L * 1024L * 1024L
                ? "likely V-Cache CCD"
                : l3Caches.Count == 1
                    ? "single L3 domain / likely standard frequency CCD"
                    : "likely frequency CCD";
            insights.Add(new CpuTopologyInsight($"L3 group {i}", $"{FormatBytes(cache.SizeBytes)}, {hint}, logical processors: {cache.LogicalProcessors.DisplayText}", CpuTopologyInsightKind.Heuristic));
        }
    }

    private static void AddHybridInsights(List<CpuTopologyInsight> insights, IReadOnlyList<CpuCoreMappingInfo> cores, IReadOnlyList<CpuCacheInstanceInfo> caches)
    {
        var classes = cores.GroupBy(core => core.EfficiencyClass).OrderBy(group => group.Key).ToList();
        if (classes.Count <= 1)
        {
            insights.Add(new CpuTopologyInsight("Hybrid topology", "No heterogeneous efficiency classes detected from Windows topology API.", CpuTopologyInsightKind.Information));
        }
        else
        {
            var maxClass = classes.Max(group => group.Key);
            var minClass = classes.Min(group => group.Key);
            foreach (var group in classes)
            {
                var role = group.Key == maxClass
                    ? "likely performance-core class"
                    : group.Key == minClass
                        ? "likely efficiency-core class"
                        : "intermediate efficiency class";
                var smtCores = group.Count(core => core.HasSmt);
                insights.Add(new CpuTopologyInsight("Hybrid efficiency class", $"class {group.Key}: {group.Count()} cores, {smtCores} SMT-capable cores, {role}", CpuTopologyInsightKind.Heuristic));
            }
        }

        var l2Clusters = caches
            .Where(cache => cache.Level == 2 && cache.LogicalProcessors.Count > 2)
            .OrderBy(cache => cache.LogicalProcessors.Group)
            .ThenBy(cache => cache.LogicalProcessors.ProcessorRange)
            .ToList();

        for (var i = 0; i < l2Clusters.Count; i++)
        {
            var cluster = l2Clusters[i];
            insights.Add(new CpuTopologyInsight($"L2 cluster {i}", $"{FormatBytes(cluster.SizeBytes)}, shared by {cluster.LogicalProcessors.Count} logical processors, logical processors: {cluster.LogicalProcessors.DisplayText}, likely E-core cluster or shared L2 domain", CpuTopologyInsightKind.Heuristic));
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024L / 1024L / 1024L} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024L / 1024L} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024L} KB";
        }

        return $"{bytes} B";
    }
}

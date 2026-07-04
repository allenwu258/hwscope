using HwScope.Core.Windows;

namespace HwScope.Core.Benchmark;

internal static class MemoryBenchmarkPlacementPlanner
{
    public static MemoryBenchmarkPlacementPlan CreatePlan(MemoryBenchmarkOptions options)
    {
        if (!options.UsePreferredCore)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Preferred core placement is disabled by options.", Math.Max(1, options.Threads), CreateUnavailableCacheRows("Preferred core placement is disabled by options."));
        }

        if (IsSingleThread(options))
        {
            return CreatePreferredSingleThreadPlan();
        }

        var topology = LogicalProcessorInformation.TryCollect();
        if (topology is null || topology.Cores.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API did not return core mapping.", Math.Max(1, options.Threads), CreateUnavailableCacheRows("Windows topology API did not return cache mapping."));
        }

        var processors = BuildProcessors(topology);
        if (processors.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.", Math.Max(1, options.Threads), CreateUnavailableCacheRows("Windows topology API returned no active logical processors."));
        }

        var preferred = SelectPreferredProcessors(processors, options);
        if (preferred.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no processors matching the requested thread placement.", Math.Max(1, options.Threads), CreateUnavailableCacheRows("Windows topology API returned no processors matching the requested thread placement."));
        }

        var cacheProcessor = SelectPreferredSingleProcessor(processors);
        var mode = options.ThreadMode.Equals("LogicalProcessors", StringComparison.OrdinalIgnoreCase)
            ? "logicalProcessors"
            : options.ThreadMode.Equals("Custom", StringComparison.OrdinalIgnoreCase)
                ? "custom"
                : "physicalCores";
        var confidence = topology.Cores.Select(core => core.EfficiencyClass).Distinct().Count() > 1 ? "heuristic" : "api";
        var reason = mode switch
        {
            "logicalProcessors" => "Selected logical processors from Windows topology, filling primary SMT units before sibling SMT units.",
            "custom" => "Selected the requested number of processors from Windows topology, preferring physical cores before SMT siblings.",
            _ => "Selected one logical processor per physical core from Windows topology."
        };

        return new MemoryBenchmarkPlacementPlan(
            Mode: mode,
            Source: "windowsTopology",
            Confidence: confidence,
            Requested: cacheProcessor ?? preferred[0],
            Workers: preferred,
            Candidates: processors,
            RequestedThreads: preferred.Count,
            Reason: reason,
            CacheRows: cacheProcessor is null
                ? CreateUnavailableCacheRows("Windows topology API returned no preferred processor for cache row placement.")
                : CreateCacheRows(topology, cacheProcessor));
    }

    public static MemoryBenchmarkPlacementPlan CreatePreferredSingleThreadPlan()
    {
        var topology = LogicalProcessorInformation.TryCollect();
        if (topology is null || topology.Cores.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API did not return core mapping.", 1, CreateUnavailableCacheRows("Windows topology API did not return cache mapping."));
        }

        var processors = BuildProcessors(topology);
        if (processors.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.", 1, CreateUnavailableCacheRows("Windows topology API returned no active logical processors."));
        }

        var preferred = SelectPreferredSingleProcessor(processors);

        if (preferred is null)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.", 1, CreateUnavailableCacheRows("Windows topology API returned no active logical processors."));
        }

        var efficiencyClasses = topology.Cores.Select(core => core.EfficiencyClass).Distinct().Count();
        var confidence = efficiencyClasses > 1 ? "heuristic" : "api";
        var reason = efficiencyClasses > 1
            ? "Selected highest Windows efficiency class, first SMT unit, and a middle core in the preferred NUMA node."
            : "Selected first SMT unit on a middle physical core from Windows topology.";

        return new MemoryBenchmarkPlacementPlan(
            Mode: "singlePreferredPhysicalCore",
            Source: "windowsTopology",
            Confidence: confidence,
            Requested: preferred,
            Workers: [preferred],
            Candidates: processors,
            RequestedThreads: 1,
            Reason: reason,
            CacheRows: CreateCacheRows(topology, preferred));
    }

    private static bool IsSingleThread(MemoryBenchmarkOptions options)
    {
        return options.ThreadMode.Equals("SingleCore", StringComparison.OrdinalIgnoreCase)
            || options.Threads == 1;
    }

    private static IReadOnlyList<MemoryBenchmarkLogicalProcessor> SelectPreferredProcessors(
        IReadOnlyList<MemoryBenchmarkLogicalProcessor> processors,
        MemoryBenchmarkOptions options)
    {
        var bestEfficiencyClass = processors.Max(processor => processor.EfficiencyClass);
        var bestClassProcessors = processors
            .Where(processor => processor.EfficiencyClass == bestEfficiencyClass)
            .ToList();
        var preferredNumaNode = bestClassProcessors
            .Select(processor => processor.NumaNodeNumber)
            .Where(node => node.HasValue)
            .Select(node => node!.Value)
            .DefaultIfEmpty(0u)
            .Min();
        var numaFiltered = options.NumaMode.Equals("Local", StringComparison.OrdinalIgnoreCase)
            ? bestClassProcessors
                .Where(processor => (processor.NumaNodeNumber ?? preferredNumaNode) == preferredNumaNode)
                .ToList()
            : bestClassProcessors;
        if (numaFiltered.Count == 0)
        {
            numaFiltered = bestClassProcessors;
        }

        var primary = numaFiltered
            .Where(processor => processor.SmtIndex == 0)
            .OrderBy(processor => processor.CoreIndex)
            .ThenBy(processor => processor.Group)
            .ThenBy(processor => processor.ProcessorNumber)
            .ToList();
        var siblings = numaFiltered
            .Where(processor => processor.SmtIndex != 0)
            .OrderBy(processor => processor.CoreIndex)
            .ThenBy(processor => processor.SmtIndex)
            .ThenBy(processor => processor.Group)
            .ThenBy(processor => processor.ProcessorNumber)
            .ToList();

        var ordered = options.ThreadMode.Equals("LogicalProcessors", StringComparison.OrdinalIgnoreCase)
            ? primary.Concat(siblings).ToList()
            : primary;
        if (ordered.Count == 0)
        {
            ordered = numaFiltered
                .OrderBy(processor => processor.CoreIndex)
                .ThenBy(processor => processor.SmtIndex)
                .ThenBy(processor => processor.Group)
                .ThenBy(processor => processor.ProcessorNumber)
                .ToList();
        }

        var requestedThreads = options.Threads <= 0
            ? ordered.Count
            : Math.Min(options.Threads, ordered.Count);
        if (requestedThreads <= 0)
        {
            return [];
        }

        return ordered.Take(requestedThreads).ToList();
    }

    private static MemoryBenchmarkLogicalProcessor? SelectPreferredSingleProcessor(
        IReadOnlyList<MemoryBenchmarkLogicalProcessor> processors)
    {
        if (processors.Count == 0)
        {
            return null;
        }

        var bestEfficiencyClass = processors.Max(processor => processor.EfficiencyClass);
        var bestClassProcessors = processors
            .Where(processor => processor.EfficiencyClass == bestEfficiencyClass)
            .ToList();
        var preferredNumaNode = bestClassProcessors
            .Select(processor => processor.NumaNodeNumber)
            .Where(node => node.HasValue)
            .Select(node => node!.Value)
            .DefaultIfEmpty(0u)
            .Min();
        var preferredProcessors = bestClassProcessors
            .Where(processor => (processor.NumaNodeNumber ?? preferredNumaNode) == preferredNumaNode)
            .Where(processor => processor.SmtIndex == 0)
            .OrderBy(processor => processor.CoreIndex)
            .ThenBy(processor => processor.Group)
            .ThenBy(processor => processor.ProcessorNumber)
            .ToList();
        if (preferredProcessors.Count == 0)
        {
            preferredProcessors = bestClassProcessors
                .OrderBy(processor => processor.CoreIndex)
                .ThenBy(processor => processor.SmtIndex)
                .ThenBy(processor => processor.Group)
                .ThenBy(processor => processor.ProcessorNumber)
                .ToList();
        }

        return preferredProcessors.Count == 0 ? null : preferredProcessors[preferredProcessors.Count / 2];
    }

    internal static IReadOnlyList<MemoryBenchmarkLogicalProcessor> BuildProcessors(LogicalProcessorTopology topology)
    {
        var result = new List<MemoryBenchmarkLogicalProcessor>();
        foreach (var core in topology.Cores)
        {
            var coreProcessors = core.Masks
                .SelectMany(mask => mask.LocalProcessorIndexes.Select(local => new { mask.Group, ProcessorNumber = local }))
                .OrderBy(processor => processor.Group)
                .ThenBy(processor => processor.ProcessorNumber)
                .ToList();

            for (var smtIndex = 0; smtIndex < coreProcessors.Count; smtIndex++)
            {
                var processor = coreProcessors[smtIndex];
                result.Add(new MemoryBenchmarkLogicalProcessor(
                    Group: processor.Group,
                    ProcessorNumber: processor.ProcessorNumber,
                    CoreIndex: core.Index,
                    PackageIndex: FindPackage(topology, processor.Group, processor.ProcessorNumber),
                    NumaNodeNumber: FindNumaNode(topology, processor.Group, processor.ProcessorNumber),
                    SmtIndex: smtIndex,
                    EfficiencyClass: core.EfficiencyClass,
                    HasSmt: core.HasSmt || coreProcessors.Count > 1));
            }
        }

        return result;
    }

    private static int? FindPackage(LogicalProcessorTopology topology, ushort group, int processorNumber)
    {
        foreach (var package in topology.Packages)
        {
            if (package.Masks.Any(mask => mask.Group == group && mask.LocalProcessorIndexes.Contains(processorNumber)))
            {
                return package.Index;
            }
        }

        return null;
    }

    private static uint? FindNumaNode(LogicalProcessorTopology topology, ushort group, int processorNumber)
    {
        foreach (var node in topology.NumaNodes)
        {
            if (node.Mask.Group == group && node.Mask.LocalProcessorIndexes.Contains(processorNumber))
            {
                return node.NodeNumber;
            }
        }

        return null;
    }

    private static IReadOnlyList<MemoryBenchmarkCacheRowPlan> CreateCacheRows(
        LogicalProcessorTopology topology,
        MemoryBenchmarkLogicalProcessor processor)
    {
        return
        [
            CreateCacheRow(topology, processor, MemoryBenchmarkRows.L1, 1, "Data"),
            CreateCacheRow(topology, processor, MemoryBenchmarkRows.L2, 2, "Unified"),
            CreateCacheRow(topology, processor, MemoryBenchmarkRows.L3, 3, "Unified")
        ];
    }

    private static IReadOnlyList<MemoryBenchmarkCacheRowPlan> CreateUnavailableCacheRows(string reason)
    {
        return
        [
            MemoryBenchmarkCacheRowPlan.Unavailable(MemoryBenchmarkRows.L1, reason),
            MemoryBenchmarkCacheRowPlan.Unavailable(MemoryBenchmarkRows.L2, reason),
            MemoryBenchmarkCacheRowPlan.Unavailable(MemoryBenchmarkRows.L3, reason)
        ];
    }

    private static MemoryBenchmarkCacheRowPlan CreateCacheRow(
        LogicalProcessorTopology topology,
        MemoryBenchmarkLogicalProcessor processor,
        string row,
        int level,
        string preferredType)
    {
        var candidates = topology.Caches
            .Where(cache => cache.Level == level)
            .Where(cache => ContainsProcessor(cache.Mask, processor.Group, processor.ProcessorNumber))
            .Where(cache => preferredType.Equals(cache.Type.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            return MemoryBenchmarkCacheRowPlan.Unavailable(row, $"Windows topology did not return a matching L{level} {preferredType} cache for the selected processor.");
        }

        var selected = row == MemoryBenchmarkRows.L3
            ? candidates.OrderByDescending(cache => cache.SizeBytes).ThenBy(cache => cache.Mask.Group).ThenBy(cache => cache.Mask.FirstProcessor).First()
            : candidates.OrderBy(cache => cache.SizeBytes).ThenBy(cache => cache.Mask.Group).ThenBy(cache => cache.Mask.FirstProcessor).First();
        var workingSetBytes = CalculateWorkingSetBytes(row, selected.SizeBytes, selected.LineSizeBytes);
        return new MemoryBenchmarkCacheRowPlan(
            Row: row,
            Available: true,
            UnavailableReason: null,
            WorkingSetBytes: workingSetBytes,
            CacheLevel: level,
            CacheSizeBytes: selected.SizeBytes,
            LineSizeBytes: selected.LineSizeBytes,
            Source: "windowsTopology");
    }

    private static bool ContainsProcessor(LogicalProcessorMask mask, ushort group, int processorNumber)
    {
        return mask.Group == group && mask.LocalProcessorIndexes.Contains(processorNumber);
    }

    private static long CalculateWorkingSetBytes(string row, long cacheSizeBytes, int lineSizeBytes)
    {
        var line = Math.Max(64L, lineSizeBytes);
        var raw = row switch
        {
            MemoryBenchmarkRows.L1 => Math.Max(8L * 1024L, Math.Min(cacheSizeBytes * 3L / 4L, cacheSizeBytes - line)),
            MemoryBenchmarkRows.L2 => Math.Max(64L * 1024L, cacheSizeBytes * 3L / 4L),
            MemoryBenchmarkRows.L3 => Math.Max(2L * 1024L * 1024L, cacheSizeBytes / 2L),
            _ => cacheSizeBytes
        };
        raw = Math.Max(line * 2L, Math.Min(raw, Math.Max(line * 2L, cacheSizeBytes - line)));
        return raw / line * line;
    }
}

internal sealed record MemoryBenchmarkPlacementPlan(
    string Mode,
    string Source,
    string Confidence,
    MemoryBenchmarkLogicalProcessor? Requested,
    IReadOnlyList<MemoryBenchmarkLogicalProcessor> Candidates,
    IReadOnlyList<MemoryBenchmarkLogicalProcessor> Workers,
    int RequestedThreads,
    string Reason,
    IReadOnlyList<MemoryBenchmarkCacheRowPlan> CacheRows)
{
    public static MemoryBenchmarkPlacementPlan Fallback(
        string reason,
        int requestedThreads,
        IReadOnlyList<MemoryBenchmarkCacheRowPlan>? cacheRows = null)
    {
        return new MemoryBenchmarkPlacementPlan(
            Mode: "currentThreadFallback",
            Source: "none",
            Confidence: "fallback",
            Requested: null,
            Candidates: [],
            Workers: [],
            RequestedThreads: Math.Max(1, requestedThreads),
            Reason: reason,
            CacheRows: cacheRows ?? []);
    }

    public int EffectiveThreads => Workers.Count > 0 ? Workers.Count : RequestedThreads;
}

internal sealed record MemoryBenchmarkCacheRowPlan(
    string Row,
    bool Available,
    string? UnavailableReason,
    long WorkingSetBytes,
    int? CacheLevel,
    long? CacheSizeBytes,
    int? LineSizeBytes,
    string Source)
{
    public static MemoryBenchmarkCacheRowPlan Unavailable(string row, string reason)
    {
        return new MemoryBenchmarkCacheRowPlan(
            Row: row,
            Available: false,
            UnavailableReason: reason,
            WorkingSetBytes: 0,
            CacheLevel: null,
            CacheSizeBytes: null,
            LineSizeBytes: null,
            Source: "windowsTopology");
    }
}

internal sealed record MemoryBenchmarkLogicalProcessor(
    ushort Group,
    int ProcessorNumber,
    int CoreIndex,
    int? PackageIndex,
    uint? NumaNodeNumber,
    int SmtIndex,
    int EfficiencyClass,
    bool HasSmt);

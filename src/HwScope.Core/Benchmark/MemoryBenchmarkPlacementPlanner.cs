using HwScope.Core.Windows;

namespace HwScope.Core.Benchmark;

internal static class MemoryBenchmarkPlacementPlanner
{
    public static MemoryBenchmarkPlacementPlan CreatePlan(MemoryBenchmarkOptions options)
    {
        if (!options.UsePreferredCore)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Preferred core placement is disabled by options.", Math.Max(1, options.Threads));
        }

        if (IsSingleThread(options))
        {
            return CreatePreferredSingleThreadPlan();
        }

        var topology = LogicalProcessorInformation.TryCollect();
        if (topology is null || topology.Cores.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API did not return core mapping.", Math.Max(1, options.Threads));
        }

        var processors = BuildProcessors(topology);
        if (processors.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.", Math.Max(1, options.Threads));
        }

        var preferred = SelectPreferredProcessors(processors, options);
        if (preferred.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no processors matching the requested thread placement.", Math.Max(1, options.Threads));
        }

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
            Requested: preferred[0],
            Workers: preferred,
            Candidates: processors,
            RequestedThreads: preferred.Count,
            Reason: reason);
    }

    public static MemoryBenchmarkPlacementPlan CreatePreferredSingleThreadPlan()
    {
        var topology = LogicalProcessorInformation.TryCollect();
        if (topology is null || topology.Cores.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API did not return core mapping.", 1);
        }

        var processors = BuildProcessors(topology);
        if (processors.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.", 1);
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

        var preferred = preferredProcessors.Count == 0
            ? null
            : preferredProcessors[preferredProcessors.Count / 2];

        if (preferred is null)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.", 1);
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
            Reason: reason);
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
}

internal sealed record MemoryBenchmarkPlacementPlan(
    string Mode,
    string Source,
    string Confidence,
    MemoryBenchmarkLogicalProcessor? Requested,
    IReadOnlyList<MemoryBenchmarkLogicalProcessor> Candidates,
    IReadOnlyList<MemoryBenchmarkLogicalProcessor> Workers,
    int RequestedThreads,
    string Reason)
{
    public static MemoryBenchmarkPlacementPlan Fallback(string reason, int requestedThreads)
    {
        return new MemoryBenchmarkPlacementPlan(
            Mode: "currentThreadFallback",
            Source: "none",
            Confidence: "fallback",
            Requested: null,
            Candidates: [],
            Workers: [],
            RequestedThreads: Math.Max(1, requestedThreads),
            Reason: reason);
    }

    public int EffectiveThreads => Workers.Count > 0 ? Workers.Count : RequestedThreads;
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

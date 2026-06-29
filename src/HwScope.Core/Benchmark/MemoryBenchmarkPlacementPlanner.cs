using HwScope.Core.Windows;

namespace HwScope.Core.Benchmark;

internal static class MemoryBenchmarkPlacementPlanner
{
    public static MemoryBenchmarkPlacementPlan CreatePreferredSingleThreadPlan()
    {
        var topology = LogicalProcessorInformation.TryCollect();
        if (topology is null || topology.Cores.Count == 0)
        {
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API did not return core mapping.");
        }

        var processors = BuildProcessors(topology);
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
            return MemoryBenchmarkPlacementPlan.Fallback("Windows topology API returned no active logical processors.");
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
            Candidates: processors,
            Reason: reason);
    }

    private static IReadOnlyList<MemoryBenchmarkLogicalProcessor> BuildProcessors(LogicalProcessorTopology topology)
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

public sealed record MemoryBenchmarkPlacementPlan(
    string Mode,
    string Source,
    string Confidence,
    MemoryBenchmarkLogicalProcessor? Requested,
    IReadOnlyList<MemoryBenchmarkLogicalProcessor> Candidates,
    string Reason)
{
    public static MemoryBenchmarkPlacementPlan Fallback(string reason)
    {
        return new MemoryBenchmarkPlacementPlan(
            Mode: "currentThreadFallback",
            Source: "none",
            Confidence: "fallback",
            Requested: null,
            Candidates: [],
            Reason: reason);
    }
}

public sealed record MemoryBenchmarkLogicalProcessor(
    ushort Group,
    int ProcessorNumber,
    int CoreIndex,
    int? PackageIndex,
    uint? NumaNodeNumber,
    int SmtIndex,
    int EfficiencyClass,
    bool HasSmt);

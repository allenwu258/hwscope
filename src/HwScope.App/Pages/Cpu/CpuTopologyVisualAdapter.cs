using HwScope.App.Topology.Model;
using HwScope.Core.Hardware.Cpu;

namespace HwScope.App.Pages.Cpu;

public static class CpuTopologyVisualAdapter
{
    public static TopologyDocument ToDocument(CpuTopologyInspectReport report, string title = "CPU Topology")
    {
        var nodes = new List<TopologyNode>();
        var groups = new List<TopologyGroup>();
        var packageGroups = BuildPackageGroups(report);

        var l3Domains = BuildL3Domains(report, packageGroups);
        groups.AddRange(packageGroups.Select(package => package.Group));
        groups.AddRange(l3Domains.Groups);

        foreach (var core in report.Cores)
        {
            var owningL3 = FindOwningL3Domain(core, l3Domains.DomainById.Values);
            var coreNode = BuildCoreNode(core, report.CacheInstances, owningL3?.Group.Id);
            nodes.Add(coreNode);

            if (owningL3 is not null)
            {
                owningL3.CoreNodeIds.Add(coreNode.Id);
            }
            else if (FindOwningPackage(core.LogicalProcessors, packageGroups) is { } package)
            {
                package.NodeIdsMutable.Add(coreNode.Id);
            }
        }

        groups = groups
            .Select(group => l3Domains.DomainById.TryGetValue(group.Id, out var domain)
                ? group with { NodeIds = domain.CoreNodeIds.OrderBy(CoreIndexFromNodeId).ToList() }
                : packageGroups.FirstOrDefault(package => package.Group.Id == group.Id) is { } package
                    ? group with { NodeIds = package.NodeIdsMutable.OrderBy(CoreIndexFromNodeId).ToList() }
                    : group)
            .ToList();

        return new TopologyDocument(
            "cpu-topology",
            title,
            nodes,
            groups,
            [],
            BuildLegend(),
            BuildNotes(report));
    }

    private static List<MutablePackageGroup> BuildPackageGroups(CpuTopologyInspectReport report)
    {
        if (report.Packages.Count == 0)
        {
            return
            [
                new MutablePackageGroup(new TopologyGroup(
                    CpuTopologyVisualIds.Package(0),
                    CpuTopologyVisualKinds.Package,
                    "Package 0",
                    ParentGroupId: null,
                    NodeIds: [],
                    ChildGroupIds: [],
                    Properties: new Dictionary<string, string>(),
                    Badges: [],
                    Style: new TopologyStyle(TopologyAccentKeys.GroupPackage)),
                    [])
            ];
        }

        return report.Packages
            .Select(package => new MutablePackageGroup(new TopologyGroup(
                CpuTopologyVisualIds.Package(package.PackageIndex),
                CpuTopologyVisualKinds.Package,
                $"Package {package.PackageIndex}",
                ParentGroupId: null,
                NodeIds: [],
                ChildGroupIds: [],
                Properties: new Dictionary<string, string>
                {
                    ["Logical processors"] = FormatMasks(package.LogicalProcessors)
                },
                Badges: BuildPackageBadges(report, package),
                Style: new TopologyStyle(TopologyAccentKeys.GroupPackage)),
                package.LogicalProcessors))
            .ToList();
    }

    private static L3DomainBuildResult BuildL3Domains(CpuTopologyInspectReport report, List<MutablePackageGroup> packageGroups)
    {
        var l3Caches = report.CacheInstances
            .Where(cache => cache.Level == 3)
            .OrderBy(cache => cache.CacheIndex)
            .ToList();

        var groups = new List<TopologyGroup>();
        var domainById = new Dictionary<string, MutableL3Domain>();

        foreach (var cache in l3Caches)
        {
            var id = CpuTopologyVisualIds.L3Domain(cache.CacheIndex);
            var badges = BuildL3Badges(cache, l3Caches);
            var parentPackageId = FindOwningPackage(cache.LogicalProcessors, packageGroups)?.Group.Id;
            var group = new TopologyGroup(
                id,
                CpuTopologyVisualKinds.L3Domain,
                $"L3 {cache.CacheType} · {FormatBytes(cache.SizeBytes)}",
                parentPackageId,
                NodeIds: [],
                ChildGroupIds: [],
                Properties: new Dictionary<string, string>
                {
                    ["Cache"] = $"L3 {cache.CacheType}",
                    ["Size"] = FormatBytes(cache.SizeBytes),
                    ["Line size"] = $"{cache.LineSizeBytes} B",
                    ["Associativity"] = FormatAssociativity(cache.Associativity),
                    ["Logical processors"] = cache.LogicalProcessors.DisplayText
                },
                Badges: badges,
                Style: new TopologyStyle(IsLikelyVCache(cache, l3Caches) ? TopologyAccentKeys.CacheL3VCache : TopologyAccentKeys.CacheL3, IsDashed: badges.Any(badge => badge.Text.Equals("heuristic", StringComparison.OrdinalIgnoreCase))),
                IsHeuristic: badges.Any(badge => badge.Text.Equals("heuristic", StringComparison.OrdinalIgnoreCase)));

            groups.Add(group);
            domainById[id] = new MutableL3Domain(group, cache);
        }

        foreach (var package in packageGroups.ToList())
        {
            var childIds = domainById.Values
                .Where(domain => package.PackageMasks.Count == 0 || MasksOverlap(domain.Cache.LogicalProcessors, package.PackageMasks))
                .Select(domain => domain.Group.Id)
                .ToList();

            if (childIds.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < packageGroups.Count; i++)
            {
                if (packageGroups[i].Group.Id == package.Group.Id)
                {
                    packageGroups[i] = packageGroups[i] with { Group = package.Group with { ChildGroupIds = childIds } };
                }
            }
        }

        return new L3DomainBuildResult(groups, domainById);
    }

    private static TopologyNode BuildCoreNode(CpuCoreMappingInfo core, IReadOnlyList<CpuCacheInstanceInfo> caches, string? l3DomainId)
    {
        var coreMasks = core.LogicalProcessors.ToList();
        var relatedIds = new List<string>();
        if (l3DomainId is not null)
        {
            relatedIds.Add(l3DomainId);
        }

        var matchingCaches = caches
            .Where(cache => MasksOverlap(cache.LogicalProcessors, coreMasks))
            .OrderBy(cache => cache.Level)
            .ThenBy(cache => cache.CacheType, StringComparer.Ordinal)
            .ToList();

        var l1Data = matchingCaches.FirstOrDefault(cache => cache.Level == 1 && cache.CacheType.Equals("Data", StringComparison.OrdinalIgnoreCase));
        var l1Instruction = matchingCaches.FirstOrDefault(cache => cache.Level == 1 && cache.CacheType.Equals("Instruction", StringComparison.OrdinalIgnoreCase));
        var l2 = matchingCaches.FirstOrDefault(cache => cache.Level == 2);
        var l3 = matchingCaches.FirstOrDefault(cache => cache.Level == 3);

        var properties = new Dictionary<string, string>
        {
            ["SMT"] = core.HasSmt ? "yes" : "no",
            ["Efficiency"] = core.EfficiencyClass.ToString(),
            ["LP"] = FormatMasks(core.LogicalProcessors)
        };

        AddCacheProperty(properties, "L1D", l1Data);
        AddCacheProperty(properties, "L1I", l1Instruction);
        AddCacheProperty(properties, "L2", l2);
        AddCacheProperty(properties, "L3", l3);

        return new TopologyNode(
            CpuTopologyVisualIds.Core(core.CoreIndex),
            CpuTopologyVisualKinds.Core,
            $"Core {core.CoreIndex:D2}",
            $"SMT {(core.HasSmt ? "yes" : "no")} · Eff {core.EfficiencyClass}",
            properties,
            relatedIds,
            new TopologyStyle(core.EfficiencyClass > 0 ? TopologyAccentKeys.CorePerformance : TopologyAccentKeys.CoreEfficiency));
    }

    private static IReadOnlyList<TopologyBadge> BuildPackageBadges(CpuTopologyInspectReport report, CpuTopologyPackageInfo package)
    {
        var badges = new List<TopologyBadge>();
        var numa = report.NumaNodes.FirstOrDefault(node => MasksOverlap(node.LogicalProcessors, package.LogicalProcessors));
        if (numa is not null)
        {
            badges.Add(new TopologyBadge($"NUMA {numa.NodeNumber}", new TopologyStyle(TopologyAccentKeys.GroupNuma)));
        }

        var group = report.Groups.FirstOrDefault(group => MasksOverlap(group.ActiveMask, package.LogicalProcessors));
        if (group is not null)
        {
            badges.Add(new TopologyBadge($"Group {group.Group}", new TopologyStyle(TopologyAccentKeys.GroupPackage)));
        }

        return badges;
    }

    private static IReadOnlyList<TopologyBadge> BuildL3Badges(CpuCacheInstanceInfo cache, IReadOnlyList<CpuCacheInstanceInfo> l3Caches)
    {
        var badges = new List<TopologyBadge>();
        if (l3Caches.Count == 0)
        {
            return badges;
        }

        var label = IsLikelyVCache(cache, l3Caches)
            ? "likely V-Cache CCD"
            : l3Caches.Count == 1
                ? "single L3 domain"
                : "likely frequency CCD";
        badges.Add(new TopologyBadge(label, new TopologyStyle(label.Contains("V-Cache", StringComparison.OrdinalIgnoreCase) ? TopologyAccentKeys.CacheL3VCache : TopologyAccentKeys.CacheL3)));

        if (l3Caches.Count > 1 || label.Contains("likely", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add(new TopologyBadge("heuristic", new TopologyStyle(TopologyAccentKeys.Heuristic, IsDashed: true)));
        }

        return badges;
    }

    private static IReadOnlyList<TopologyLegendItem> BuildLegend()
    {
        return
        [
            new TopologyLegendItem("Package", "Physical CPU package or socket.", new TopologyStyle(TopologyAccentKeys.GroupPackage)),
            new TopologyLegendItem("L3 domain", "Shared last-level cache domain.", new TopologyStyle(TopologyAccentKeys.CacheL3)),
            new TopologyLegendItem("V-Cache hint", "Likely AMD V-Cache CCD inferred from L3 size.", new TopologyStyle(TopologyAccentKeys.CacheL3VCache)),
            new TopologyLegendItem("Core", "Physical core tile with logical processor and cache properties.", new TopologyStyle(TopologyAccentKeys.CoreEfficiency)),
            new TopologyLegendItem("Heuristic", "Inferred label that needs external validation.", new TopologyStyle(TopologyAccentKeys.Heuristic, IsDashed: true))
        ];
    }

    private static IReadOnlyList<TopologyNote> BuildNotes(CpuTopologyInspectReport report)
    {
        var notes = report.Notes.Select(note => new TopologyNote(note)).ToList();
        notes.AddRange(report.Insights
            .Where(insight => insight.Kind == CpuTopologyInsightKind.Heuristic)
            .Select(insight => new TopologyNote($"{insight.Title}: {insight.Detail}", TopologyNoteKind.Heuristic)));
        return notes;
    }

    private static MutableL3Domain? FindOwningL3Domain(CpuCoreMappingInfo core, IEnumerable<MutableL3Domain> domains)
    {
        return domains.FirstOrDefault(domain => core.LogicalProcessors.Any(mask => MasksOverlap(domain.Cache.LogicalProcessors, mask)));
    }

    private static MutablePackageGroup? FindOwningPackage(IEnumerable<CpuProcessorMaskView> masks, IReadOnlyList<MutablePackageGroup> packages)
    {
        return packages.FirstOrDefault(package => package.PackageMasks.Count == 0 || package.PackageMasks.Any(packageMask => MasksOverlap(packageMask, masks)));
    }

    private static MutablePackageGroup? FindOwningPackage(CpuProcessorMaskView mask, IReadOnlyList<MutablePackageGroup> packages)
    {
        return FindOwningPackage([mask], packages);
    }

    private static void AddCacheProperty(IDictionary<string, string> properties, string label, CpuCacheInstanceInfo? cache)
    {
        if (cache is null)
        {
            return;
        }

        properties[label] = $"{FormatBytes(cache.SizeBytes)}, {FormatAssociativity(cache.Associativity)}, line {cache.LineSizeBytes} B";
    }

    private static bool IsLikelyVCache(CpuCacheInstanceInfo cache, IReadOnlyList<CpuCacheInstanceInfo> l3Caches)
    {
        return l3Caches.Count > 1 && cache.SizeBytes >= 64L * 1024L * 1024L;
    }

    private static bool MasksOverlap(CpuProcessorMaskView left, IEnumerable<CpuProcessorMaskView> right)
    {
        return right.Any(mask => MasksOverlap(left, mask));
    }

    private static bool MasksOverlap(CpuProcessorMaskView left, CpuProcessorMaskView right)
    {
        if (left.Group != right.Group)
        {
            return false;
        }

        return TryParseHexMask(left.HexMask, out var leftMask)
            && TryParseHexMask(right.HexMask, out var rightMask)
            && (leftMask & rightMask) != 0;
    }

    private static bool TryParseHexMask(string value, out ulong mask)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out mask);
    }

    private static string FormatMasks(IEnumerable<CpuProcessorMaskView> masks)
    {
        return string.Join("; ", masks.Select(mask => mask.DisplayText));
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

    private static string FormatAssociativity(int associativity)
    {
        return associativity == 0xff ? "full" : $"{associativity}-way";
    }

    private static int CoreIndexFromNodeId(string nodeId)
    {
        var lastDash = nodeId.LastIndexOf('-');
        return lastDash >= 0 && int.TryParse(nodeId[(lastDash + 1)..], out var index) ? index : int.MaxValue;
    }

    private sealed record MutablePackageGroup(TopologyGroup Group, IReadOnlyList<CpuProcessorMaskView> PackageMasks)
    {
        public List<string> NodeIdsMutable { get; } = [];
    }

    private sealed record MutableL3Domain(TopologyGroup Group, CpuCacheInstanceInfo Cache)
    {
        public List<string> CoreNodeIds { get; } = [];
    }

    private sealed record L3DomainBuildResult(IReadOnlyList<TopologyGroup> Groups, IReadOnlyDictionary<string, MutableL3Domain> DomainById);
}

internal static class CpuTopologyVisualKinds
{
    public const string Package = "cpu.package";
    public const string L3Domain = "cpu.l3Domain";
    public const string Core = "cpu.core";
}

internal static class CpuTopologyVisualIds
{
    public static string Package(int packageIndex)
    {
        return $"cpu-package-{packageIndex}";
    }

    public static string L3Domain(int cacheIndex)
    {
        return $"cpu-l3-{cacheIndex}";
    }

    public static string Core(int coreIndex)
    {
        return $"cpu-core-{coreIndex:D2}";
    }
}

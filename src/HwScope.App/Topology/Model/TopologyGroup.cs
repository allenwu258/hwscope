namespace HwScope.App.Topology.Model;

public sealed record TopologyGroup(
    string Id,
    string Kind,
    string Label,
    string? ParentGroupId,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> ChildGroupIds,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<TopologyBadge> Badges,
    TopologyStyle Style,
    bool IsHeuristic = false);

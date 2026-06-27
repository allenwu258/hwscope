namespace HwScope.App.Topology.Model;

public sealed record TopologyNode(
    string Id,
    string Kind,
    string Label,
    string? Subtitle,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<string> RelatedIds,
    TopologyStyle Style);

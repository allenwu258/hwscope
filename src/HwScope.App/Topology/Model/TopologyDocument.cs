namespace HwScope.App.Topology.Model;

public sealed record TopologyDocument(
    string Id,
    string Title,
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<TopologyGroup> Groups,
    IReadOnlyList<TopologyEdge> Edges,
    IReadOnlyList<TopologyLegendItem> Legend,
    IReadOnlyList<TopologyNote> Notes)
{
    public static TopologyDocument Empty { get; } = new(
        "empty",
        "Topology",
        [],
        [],
        [],
        [],
        []);
}

namespace HwScope.App.Topology.Model;

public sealed record TopologyEdge(
    string Id,
    string FromId,
    string ToId,
    string Kind,
    TopologyStyle Style);

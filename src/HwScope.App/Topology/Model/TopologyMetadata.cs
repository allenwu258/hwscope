namespace HwScope.App.Topology.Model;

public sealed record TopologyBadge(
    string Text,
    TopologyStyle Style);

public sealed record TopologyLegendItem(
    string Label,
    string Description,
    TopologyStyle Style);

public sealed record TopologyNote(
    string Text,
    TopologyNoteKind Kind = TopologyNoteKind.Information);

public enum TopologyNoteKind
{
    Information,
    Heuristic,
    Warning
}

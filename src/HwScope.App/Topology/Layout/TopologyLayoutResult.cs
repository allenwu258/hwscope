using System.Windows;

namespace HwScope.App.Topology.Layout;

public sealed record TopologyLayoutResult(
    Size CanvasSize,
    IReadOnlyDictionary<string, Rect> GroupBounds,
    IReadOnlyDictionary<string, Rect> NodeBounds,
    IReadOnlyDictionary<string, Point> EdgePorts)
{
    public static TopologyLayoutResult Empty { get; } = new(
        new Size(1, 1),
        new Dictionary<string, Rect>(),
        new Dictionary<string, Rect>(),
        new Dictionary<string, Point>());
}

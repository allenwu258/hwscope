using System.Windows;
using HwScope.App.Topology.Model;

namespace HwScope.App.Topology.Layout;

public sealed class HierarchicalTopologyLayoutEngine : ITopologyLayoutEngine
{
    private const double OuterMargin = 18;
    private const double HorizontalGap = 18;
    private const double VerticalGap = 48;

    public TopologyLayoutResult Layout(TopologyDocument document, TopologyLayoutOptions options)
    {
        if (document.Nodes.Count == 0)
        {
            return TopologyLayoutResult.Empty;
        }

        var nodeById = document.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var nodeOrder = document.Nodes.Select((node, index) => (node.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var children = nodeById.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in document.Edges)
        {
            if (!nodeById.ContainsKey(edge.FromId) || !nodeById.ContainsKey(edge.ToId) || parent.ContainsKey(edge.ToId))
            {
                continue;
            }

            children[edge.FromId].Add(edge.ToId);
            parent[edge.ToId] = edge.FromId;
        }

        foreach (var childList in children.Values)
        {
            childList.Sort((left, right) => nodeOrder[left].CompareTo(nodeOrder[right]));
        }

        var roots = nodeById.Keys.Where(id => !parent.ContainsKey(id)).OrderBy(id => nodeOrder[id]).ToList();
        if (roots.Count == 0)
        {
            roots.Add(nodeById.Keys.OrderBy(id => id, StringComparer.Ordinal).First());
        }

        var nodeSize = options.Density == TopologyDensity.Compact
            ? new Size(148, 76)
            : new Size(176, 104);
        var nodeBounds = new Dictionary<string, Rect>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var nextLeafX = OuterMargin;

        foreach (var root in roots)
        {
            LayoutSubtree(root, 0, children, nodeSize, nodeBounds, visited, ref nextLeafX);
            nextLeafX += HorizontalGap * 2;
        }

        foreach (var orphan in nodeById.Keys.Where(id => !visited.Contains(id)).OrderBy(id => nodeOrder[id]))
        {
            LayoutSubtree(orphan, 0, children, nodeSize, nodeBounds, visited, ref nextLeafX);
            nextLeafX += HorizontalGap * 2;
        }

        var width = Math.Max(options.AvailableWidth, nodeBounds.Values.Max(bounds => bounds.Right) + OuterMargin);
        var height = nodeBounds.Values.Max(bounds => bounds.Bottom) + OuterMargin;
        var ports = nodeBounds.ToDictionary(
            pair => pair.Key,
            pair => new Point(pair.Value.Left + pair.Value.Width / 2, pair.Value.Top + pair.Value.Height / 2),
            StringComparer.Ordinal);
        return new TopologyLayoutResult(new Size(width, height), new Dictionary<string, Rect>(), nodeBounds, ports);
    }

    private static double LayoutSubtree(
        string nodeId,
        int depth,
        IReadOnlyDictionary<string, List<string>> children,
        Size nodeSize,
        IDictionary<string, Rect> nodeBounds,
        ISet<string> visited,
        ref double nextLeafX)
    {
        if (!visited.Add(nodeId))
        {
            return nextLeafX;
        }

        var childCenters = new List<double>();
        foreach (var childId in children[nodeId])
        {
            childCenters.Add(LayoutSubtree(childId, depth + 1, children, nodeSize, nodeBounds, visited, ref nextLeafX));
        }

        double centerX;
        if (childCenters.Count == 0)
        {
            centerX = nextLeafX + nodeSize.Width / 2;
            nextLeafX += nodeSize.Width + HorizontalGap;
        }
        else
        {
            centerX = (childCenters[0] + childCenters[^1]) / 2;
        }

        var top = OuterMargin + depth * (nodeSize.Height + VerticalGap);
        nodeBounds[nodeId] = new Rect(centerX - nodeSize.Width / 2, top, nodeSize.Width, nodeSize.Height);
        return centerX;
    }
}

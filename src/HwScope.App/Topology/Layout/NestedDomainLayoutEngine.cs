using System.Windows;
using HwScope.App.Topology.Model;

namespace HwScope.App.Topology.Layout;

public sealed class NestedDomainLayoutEngine : ITopologyLayoutEngine
{
    private const double OuterMargin = 16;
    private const double GroupPadding = 14;
    private const double GroupHeaderHeight = 36;
    private const double ItemGap = 10;
    private const double GroupGap = 14;

    public TopologyLayoutResult Layout(TopologyDocument document, TopologyLayoutOptions options)
    {
        if (document.Groups.Count == 0 && document.Nodes.Count == 0)
        {
            return TopologyLayoutResult.Empty;
        }

        var groupBounds = new Dictionary<string, Rect>();
        var nodeBounds = new Dictionary<string, Rect>();
        var groupById = document.Groups.ToDictionary(group => group.Id);
        var nodeById = document.Nodes.ToDictionary(node => node.Id);
        var rootGroups = document.Groups
            .Where(group => string.IsNullOrWhiteSpace(group.ParentGroupId) || !groupById.ContainsKey(group.ParentGroupId))
            .ToList();

        var availableWidth = Math.Max(360, options.AvailableWidth);
        var cursorY = OuterMargin;
        var contentWidth = availableWidth - OuterMargin * 2;

        foreach (var root in rootGroups)
        {
            var size = LayoutGroup(root, OuterMargin, cursorY, contentWidth, options, groupById, nodeById, groupBounds, nodeBounds);
            cursorY += size.Height + GroupGap;
        }

        var orphanNodes = document.Nodes
            .Where(node => !document.Groups.Any(group => group.NodeIds.Contains(node.Id)))
            .ToList();

        if (orphanNodes.Count > 0)
        {
            var orphanSize = LayoutNodes(orphanNodes, OuterMargin, cursorY, contentWidth, options, nodeBounds);
            cursorY += orphanSize.Height + GroupGap;
        }

        var canvasHeight = Math.Max(1, cursorY + OuterMargin);
        var canvasSize = new Size(availableWidth, canvasHeight);
        return new TopologyLayoutResult(canvasSize, groupBounds, nodeBounds, BuildEdgePorts(nodeBounds));
    }

    private static Size LayoutGroup(
        TopologyGroup group,
        double x,
        double y,
        double width,
        TopologyLayoutOptions options,
        IReadOnlyDictionary<string, TopologyGroup> groupById,
        IReadOnlyDictionary<string, TopologyNode> nodeById,
        IDictionary<string, Rect> groupBounds,
        IDictionary<string, Rect> nodeBounds)
    {
        var innerX = x + GroupPadding;
        var innerY = y + GroupPadding + GroupHeaderHeight;
        var innerWidth = Math.Max(220, width - GroupPadding * 2);
        var cursorY = innerY;

        foreach (var childGroupId in group.ChildGroupIds)
        {
            if (!groupById.TryGetValue(childGroupId, out var child))
            {
                continue;
            }

            var childSize = LayoutGroup(child, innerX, cursorY, innerWidth, options, groupById, nodeById, groupBounds, nodeBounds);
            cursorY += childSize.Height + GroupGap;
        }

        var nodes = group.NodeIds
            .Select(id => nodeById.TryGetValue(id, out var node) ? node : null)
            .Where(node => node is not null)
            .Cast<TopologyNode>()
            .ToList();

        if (nodes.Count > 0)
        {
            var nodeSize = LayoutNodes(nodes, innerX, cursorY, innerWidth, options, nodeBounds);
            cursorY += nodeSize.Height + ItemGap;
        }

        var height = Math.Max(GroupHeaderHeight + GroupPadding * 2, cursorY - y + GroupPadding);
        var bounds = new Rect(x, y, width, height);
        groupBounds[group.Id] = bounds;
        return bounds.Size;
    }

    private static Size LayoutNodes(
        IReadOnlyList<TopologyNode> nodes,
        double x,
        double y,
        double width,
        TopologyLayoutOptions options,
        IDictionary<string, Rect> nodeBounds)
    {
        var nodeSize = GetNodeSize(options.Density);
        var columns = Math.Max(1, (int)Math.Floor((width + ItemGap) / (nodeSize.Width + ItemGap)));
        var cursorX = x;
        var cursorY = y;
        var column = 0;

        foreach (var node in nodes)
        {
            nodeBounds[node.Id] = new Rect(cursorX, cursorY, nodeSize.Width, nodeSize.Height);
            column++;
            if (column >= columns)
            {
                column = 0;
                cursorX = x;
                cursorY += nodeSize.Height + ItemGap;
            }
            else
            {
                cursorX += nodeSize.Width + ItemGap;
            }
        }

        var rows = (int)Math.Ceiling(nodes.Count / (double)columns);
        var height = rows * nodeSize.Height + Math.Max(0, rows - 1) * ItemGap;
        return new Size(width, height);
    }

    private static Size GetNodeSize(TopologyDensity density)
    {
        return density == TopologyDensity.Compact
            ? new Size(132, 82)
            : new Size(172, 112);
    }

    private static IReadOnlyDictionary<string, Point> BuildEdgePorts(IReadOnlyDictionary<string, Rect> nodeBounds)
    {
        return nodeBounds.ToDictionary(
            pair => pair.Key,
            pair => new Point(pair.Value.Left + pair.Value.Width / 2, pair.Value.Top + pair.Value.Height / 2));
    }
}

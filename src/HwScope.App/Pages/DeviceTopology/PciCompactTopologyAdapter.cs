using HwScope.App.Topology.Model;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.App.Pages.DeviceTopology;

public static class PciCompactTopologyAdapter
{
    public static HashSet<string> CreateDefaultExpansion(PciTopologySnapshot snapshot)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Nodes)
        {
            if (node.ChildNodeIds.Count > 0 && HasMeaningfulEndpointDescendant(node.NodeId, nodes, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                expanded.Add(node.NodeId);
            }
        }

        return expanded;
    }

    public static TopologyDocument ToDocument(PciTopologySnapshot snapshot, IReadOnlySet<string> expandedNodeIds)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootId in snapshot.RootNodeIds)
        {
            if (nodes.TryGetValue(rootId, out var root) && IsCompactRootVisible(root))
            {
                AddVisible(rootId, nodes, expandedNodeIds, visibleIds);
            }
        }

        var visibleNodes = snapshot.Nodes
            .Where(node => visibleIds.Contains(node.NodeId))
            .Select(node => ToVisualNode(node, expandedNodeIds, nodes))
            .ToList();
        var edges = snapshot.Nodes
            .Where(node => visibleIds.Contains(node.NodeId) && node.ParentNodeId is not null && visibleIds.Contains(node.ParentNodeId))
            .Select(node => new TopologyEdge(
                $"pci-edge:{node.ParentNodeId}:{node.NodeId}",
                node.ParentNodeId!,
                node.NodeId,
                "pci.parent",
                new TopologyStyle(node.Identity.Status.HasProblem ? TopologyAccentKeys.Heuristic : TopologyAccentKeys.DevicePcie)))
            .ToList();

        return new TopologyDocument(
            "pci-compact-topology",
            "PCI Express",
            visibleNodes,
            [],
            edges,
            [],
            snapshot.Diagnostics.Entries
                .Where(entry => entry.Severity != DeviceTopologyDiagnosticSeverity.Information)
                .Take(5)
                .Select(entry => new TopologyNote(entry.Message, TopologyNoteKind.Warning))
                .ToList());
    }

    private static TopologyNode ToVisualNode(
        PciTopologyNode node,
        IReadOnlySet<string> expandedNodeIds,
        IReadOnlyDictionary<string, PciTopologyNode> nodes)
    {
        var hiddenCount = expandedNodeIds.Contains(node.NodeId) ? 0 : CountDescendants(node.NodeId, nodes);
        var properties = new Dictionary<string, string>();
        if (node.Link.CurrentGeneration.IsAvailable || node.Link.CurrentWidth.IsAvailable)
        {
            properties["链路"] = string.Join(' ', new[]
            {
                node.Link.CurrentGeneration.IsAvailable ? node.Link.CurrentGeneration.DisplayText : null,
                node.Link.CurrentWidth.IsAvailable ? node.Link.CurrentWidth.DisplayText : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        if (hiddenCount > 0)
        {
            properties["收起"] = $"{hiddenCount} 个节点";
        }
        else if (node.Identity.Status.HasProblem)
        {
            properties["状态"] = $"Problem {node.Identity.Status.ProblemCode}";
        }

        return new TopologyNode(
            node.NodeId,
            node.Kind switch
            {
                PciTopologyNodeKind.Bridge => "pci.bridge",
                PciTopologyNodeKind.Root => "pci.root",
                _ => "pci.endpoint"
            },
            BuildCompactLabel(node),
            node.Address?.ToString() ?? node.Class.DisplayName,
            properties,
            node.ParentNodeId is null ? [] : [node.ParentNodeId],
            new TopologyStyle(node.Identity.Status.HasProblem
                ? TopologyAccentKeys.Heuristic
                : node.Kind is PciTopologyNodeKind.Root or PciTopologyNodeKind.Bridge
                    ? TopologyAccentKeys.GroupPcieRoot
                    : TopologyAccentKeys.DevicePcie),
            CanExpand: node.ChildNodeIds.Count > 0,
            IsExpanded: expandedNodeIds.Contains(node.NodeId),
            HiddenChildCount: hiddenCount);
    }

    private static void AddVisible(
        string nodeId,
        IReadOnlyDictionary<string, PciTopologyNode> nodes,
        IReadOnlySet<string> expandedNodeIds,
        ISet<string> visibleIds)
    {
        if (!nodes.TryGetValue(nodeId, out var node) || !visibleIds.Add(nodeId) || !expandedNodeIds.Contains(nodeId))
        {
            return;
        }

        foreach (var childId in node.ChildNodeIds)
        {
            AddVisible(childId, nodes, expandedNodeIds, visibleIds);
        }
    }

    private static bool HasMeaningfulEndpointDescendant(
        string nodeId,
        IReadOnlyDictionary<string, PciTopologyNode> nodes,
        ISet<string> visited)
    {
        if (!nodes.TryGetValue(nodeId, out var node) || !visited.Add(nodeId))
        {
            return false;
        }

        foreach (var childId in node.ChildNodeIds)
        {
            if (!nodes.TryGetValue(childId, out var child))
            {
                continue;
            }

            if (child.Kind == PciTopologyNodeKind.Endpoint || HasMeaningfulEndpointDescendant(childId, nodes, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountDescendants(string nodeId, IReadOnlyDictionary<string, PciTopologyNode> nodes)
    {
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            return 0;
        }

        return node.ChildNodeIds.Sum(childId => 1 + CountDescendants(childId, nodes));
    }

    private static bool IsCompactRootVisible(PciTopologyNode node)
    {
        return node.ChildNodeIds.Count > 0
            || node.Class.BaseClass.HasValue
            || node.Identity.Status.HasProblem;
    }

    private static string BuildCompactLabel(PciTopologyNode node)
    {
        var coordinate = node.Address?.ToString();
        return node.DeviceType switch
        {
            PciDeviceType.PciExpressRootPort => $"Root Port {ShortCoordinate(node.Address)}",
            PciDeviceType.PciExpressUpstreamSwitchPort => $"Upstream {ShortCoordinate(node.Address)}",
            PciDeviceType.PciExpressDownstreamSwitchPort => $"Downstream {ShortCoordinate(node.Address)}",
            PciDeviceType.PciConventionalBridge or PciDeviceType.PciXBridge => $"PCI Bridge {coordinate}",
            _ => node.Identity.DisplayName
        };
    }

    private static string ShortCoordinate(PciAddress? address)
    {
        return address is null ? "?" : $"{address.Device:X2}.{address.Function}";
    }
}

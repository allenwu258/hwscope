using HwScope.App.Topology.Model;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.App.Pages.DeviceTopology;

public static class UsbCompactTopologyAdapter
{
    public static HashSet<string> CreateDefaultExpansion(UsbTopologySnapshot snapshot)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Nodes)
        {
            if (node.ChildNodeIds.Count > 0 && HasConnectedDescendant(node.NodeId, nodes))
            {
                expanded.Add(node.NodeId);
            }
        }

        return expanded;
    }

    public static TopologyDocument ToDocument(UsbTopologySnapshot snapshot, IReadOnlySet<string> expandedNodeIds)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedControllerIds = snapshot.HostControllerNodeIds
            .OrderByDescending(controllerId => HasConnectedDescendant(controllerId, nodes))
            .ToArray();
        foreach (var controllerId in orderedControllerIds)
        {
            AddVisible(controllerId, nodes, expandedNodeIds, visibleIds);
        }

        var controllerRank = orderedControllerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var visibleNodes = snapshot.Nodes
            .Where(node => visibleIds.Contains(node.NodeId))
            .OrderBy(node => controllerRank.GetValueOrDefault(node.ControllerNodeId, int.MaxValue))
            .Select(node => ToVisualNode(node, expandedNodeIds, nodes))
            .ToArray();
        var edges = snapshot.Nodes
            .Where(node => visibleIds.Contains(node.NodeId)
                && node.ParentNodeId is not null
                && visibleIds.Contains(node.ParentNodeId))
            .Select(node => new TopologyEdge(
                $"usb-edge:{node.ParentNodeId}:{node.NodeId}",
                node.ParentNodeId!,
                node.NodeId,
                "usb.parent",
                new TopologyStyle(Accent(node))))
            .ToArray();

        return new TopologyDocument(
            "usb-compact-topology",
            "USB",
            visibleNodes,
            [],
            edges,
            [],
            snapshot.Diagnostics.Entries
                .Where(entry => entry.Severity != DeviceTopologyDiagnosticSeverity.Information)
                .Take(5)
                .Select(entry => new TopologyNote(entry.Message, TopologyNoteKind.Warning))
                .ToArray());
    }

    private static TopologyNode ToVisualNode(
        UsbTopologyNode node,
        IReadOnlySet<string> expandedNodeIds,
        IReadOnlyDictionary<string, UsbTopologyNode> nodes)
    {
        var hiddenCount = expandedNodeIds.Contains(node.NodeId) ? 0 : CountDescendants(node.NodeId, nodes);
        var properties = new Dictionary<string, string>();
        if (node.Port is not null)
        {
            properties["端口"] = node.Port.PortChain;
            if (node.Port.ConnectionStatus != UsbConnectionStatus.NoDeviceConnected)
            {
                properties["连接"] = FormatSpeed(node.Port.ConnectionSpeed);
            }
        }

        if (hiddenCount > 0)
        {
            properties["隐藏"] = $"收起 {hiddenCount}";
        }

        var hasProblem = node.Identity?.Status.HasProblem == true
            || node.Port?.ConnectionStatus is not null
                and not UsbConnectionStatus.NoDeviceConnected
                and not UsbConnectionStatus.DeviceConnected;
        if (hasProblem)
        {
            properties["状态"] = node.Identity?.Status.ProblemCode is { } code
                ? $"Problem {code}"
                : node.Port?.ConnectionStatus.ToString() ?? "异常";
        }

        return new TopologyNode(
            node.NodeId,
            node.Kind switch
            {
                UsbTopologyNodeKind.HostController => "usb.controller",
                UsbTopologyNodeKind.RootHub => "usb.root-hub",
                UsbTopologyNodeKind.Hub => "usb.hub",
                UsbTopologyNodeKind.Port => "usb.port",
                UsbTopologyNodeKind.Device => "usb.device",
                _ => "usb.error"
            },
            node.Kind == UsbTopologyNodeKind.Port
                && node.Port?.ConnectionStatus == UsbConnectionStatus.NoDeviceConnected
                    ? $"{node.DisplayName} · 空闲"
                    : node.DisplayName,
            BuildSubtitle(node),
            properties,
            node.ParentNodeId is null ? [] : [node.ParentNodeId],
            new TopologyStyle(hasProblem ? TopologyAccentKeys.Heuristic : Accent(node)),
            CanExpand: node.ChildNodeIds.Count > 0,
            IsExpanded: expandedNodeIds.Contains(node.NodeId),
            HiddenChildCount: hiddenCount);
    }

    private static string BuildSubtitle(UsbTopologyNode node)
    {
        if (node.DeviceDescriptor is not null)
        {
            return $"{node.DeviceDescriptor.VendorProduct} · USB {node.DeviceDescriptor.UsbVersion}";
        }

        if (node.Hub is not null)
        {
            return $"{node.Hub.PortCount} ports";
        }

        if (node.Port is not null)
        {
            return node.Port.ConnectionStatus == UsbConnectionStatus.NoDeviceConnected
                ? "未连接"
                : $"{node.Port.ConnectionStatus} · {FormatSpeed(node.Port.ConnectionSpeed)}";
        }

        return node.Identity?.DeviceDescription ?? string.Empty;
    }

    private static string Accent(UsbTopologyNode node)
    {
        return node.Kind switch
        {
            UsbTopologyNodeKind.HostController => TopologyAccentKeys.GroupUsbController,
            UsbTopologyNodeKind.RootHub or UsbTopologyNodeKind.Hub => TopologyAccentKeys.GroupUsbHub,
            UsbTopologyNodeKind.Port => TopologyAccentKeys.PortUsb,
            _ => TopologyAccentKeys.DeviceUsb
        };
    }

    private static string FormatSpeed(UsbConnectionSpeed speed)
    {
        return speed switch
        {
            UsbConnectionSpeed.Low => "Low-Speed",
            UsbConnectionSpeed.Full => "Full-Speed",
            UsbConnectionSpeed.High => "High-Speed",
            UsbConnectionSpeed.Super => "SuperSpeed",
            UsbConnectionSpeed.SuperPlus => "SuperSpeedPlus",
            _ => "速率未知"
        };
    }

    private static void AddVisible(
        string nodeId,
        IReadOnlyDictionary<string, UsbTopologyNode> nodes,
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

    private static bool HasConnectedDescendant(
        string nodeId,
        IReadOnlyDictionary<string, UsbTopologyNode> nodes)
    {
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        foreach (var childId in node.ChildNodeIds)
        {
            if (!nodes.TryGetValue(childId, out var child))
            {
                continue;
            }

            if (child.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub
                || HasConnectedDescendant(childId, nodes))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountDescendants(string nodeId, IReadOnlyDictionary<string, UsbTopologyNode> nodes)
    {
        return !nodes.TryGetValue(nodeId, out var node)
            ? 0
            : node.ChildNodeIds.Sum(childId => 1 + CountDescendants(childId, nodes));
    }
}

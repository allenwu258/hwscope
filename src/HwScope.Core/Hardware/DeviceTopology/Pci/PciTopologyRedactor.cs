namespace HwScope.Core.Hardware.DeviceTopology.Pci;

public static class PciTopologyRedactor
{
    public static PciTopologySnapshot RedactSensitiveIds(PciTopologySnapshot snapshot)
    {
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deviceIndex = 0;
        foreach (var node in snapshot.Nodes)
        {
            idMap[node.NodeId] = node.Identity.Enumerator == "PCIROOT"
                ? node.NodeId
                : $"pci-node-{++deviceIndex:D4}";
        }

        string? MapId(string? nodeId)
        {
            return nodeId is not null && idMap.TryGetValue(nodeId, out var mapped) ? mapped : null;
        }

        var nodes = snapshot.Nodes.Select(node =>
        {
            var mappedNodeId = idMap[node.NodeId];
            var identity = node.Identity with
            {
                StableId = mappedNodeId,
                InstanceId = "[redacted]",
                ContainerId = null,
                HardwareIds = [],
                CompatibleIds = [],
                LocationPaths = []
            };
            return node with
            {
                NodeId = mappedNodeId,
                ParentNodeId = MapId(node.ParentNodeId),
                ChildNodeIds = node.ChildNodeIds.Select(childId => idMap[childId]).ToList(),
                Identity = identity
            };
        }).ToList();
        var sensitiveValues = snapshot.Nodes
            .Where(node => node.Identity.Enumerator == "PCI")
            .SelectMany(node => new[] { node.NodeId, node.Identity.InstanceId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToList();
        string RedactMessage(string message)
        {
            return sensitiveValues.Aggregate(
                message,
                (current, sensitive) => current.Replace(sensitive, "[redacted]", StringComparison.OrdinalIgnoreCase));
        }

        var diagnostics = snapshot.Diagnostics with
        {
            Entries = snapshot.Diagnostics.Entries.Select(entry => entry with
            {
                NodeId = MapId(entry.NodeId),
                Message = RedactMessage(entry.Message)
            }).ToList()
        };

        return snapshot with
        {
            Nodes = nodes,
            RootNodeIds = snapshot.RootNodeIds.Select(rootId => idMap[rootId]).ToList(),
            Diagnostics = diagnostics
        };
    }
}

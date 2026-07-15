namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public static class UsbTopologyRedactor
{
    public static UsbTopologySnapshot RedactSensitiveIds(UsbTopologySnapshot snapshot)
    {
        var map = snapshot.Nodes
            .Select((node, index) => (node.NodeId, Redacted: $"usb-node-{index + 1:D4}"))
            .ToDictionary(item => item.NodeId, item => item.Redacted, StringComparer.OrdinalIgnoreCase);

        var nodes = snapshot.Nodes.Select(node => node with
        {
            NodeId = map[node.NodeId],
            ParentNodeId = node.ParentNodeId is null ? null : map.GetValueOrDefault(node.ParentNodeId),
            ChildNodeIds = node.ChildNodeIds.Where(map.ContainsKey).Select(id => map[id]).ToArray(),
            Identity = RedactIdentity(node.Identity, map[node.NodeId]),
            ControllerNodeId = map.GetValueOrDefault(node.ControllerNodeId, string.Empty),
            DevicePath = string.Empty,
            DriverKey = string.Empty,
            Hub = node.Hub is null ? null : node.Hub with { SymbolicName = string.Empty },
            Port = node.Port is null ? null : node.Port with { CompanionHubSymbolicName = string.Empty },
            AttachmentId = node.AttachmentId is not null && map.TryGetValue(node.AttachmentId, out var attachmentId)
                ? attachmentId
                : null
        }).ToArray();

        var sensitiveValues = snapshot.Nodes
            .SelectMany(node => new[]
            {
                node.NodeId,
                node.AttachmentId,
                node.ControllerNodeId,
                node.DevicePath,
                node.DriverKey,
                node.Identity?.StableId,
                node.Identity?.InstanceId,
                node.Hub?.SymbolicName,
                node.Port?.CompanionHubSymbolicName
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToArray();
        string RedactMessage(string message)
        {
            return sensitiveValues.Aggregate(
                message,
                (current, sensitive) => current.Replace(sensitive, "[redacted]", StringComparison.OrdinalIgnoreCase));
        }

        var diagnostics = new DeviceTopologyDiagnostics(snapshot.Diagnostics.Entries.Select(entry => entry with
        {
            NodeId = entry.NodeId is not null && map.TryGetValue(entry.NodeId, out var redactedId)
                ? redactedId
                : null,
            Message = RedactMessage(entry.Message)
        }).ToArray());

        return new UsbTopologySnapshot(
            nodes,
            snapshot.HostControllerNodeIds.Where(map.ContainsKey).Select(id => map[id]).ToArray(),
            diagnostics,
            snapshot.GeneratedAt);
    }

    private static PnpDeviceIdentity? RedactIdentity(PnpDeviceIdentity? identity, string stableId)
    {
        return identity is null
            ? null
            : identity with
            {
                StableId = stableId,
                InstanceId = "[redacted]",
                ContainerId = null,
                HardwareIds = [],
                CompatibleIds = [],
                LocationPaths = []
            };
    }
}

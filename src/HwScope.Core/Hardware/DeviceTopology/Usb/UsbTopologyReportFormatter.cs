using System.Text;

namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public static class UsbTopologyReportFormatter
{
    public static string Format(UsbTopologySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("USB Physical Port Topology");
        builder.AppendLine("==========================");
        builder.AppendLine($"Generated: {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Controllers: {snapshot.HostControllerNodeIds.Count}");
        builder.AppendLine($"Nodes: {snapshot.Nodes.Count}");
        builder.AppendLine();

        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        foreach (var controllerId in snapshot.HostControllerNodeIds)
        {
            AppendNode(builder, controllerId, nodes, 0);
        }

        if (snapshot.Diagnostics.Entries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            builder.AppendLine("-----------");
            foreach (var diagnostic in snapshot.Diagnostics.Entries)
            {
                builder.AppendLine($"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendNode(
        StringBuilder builder,
        string nodeId,
        IReadOnlyDictionary<string, UsbTopologyNode> nodes,
        int depth)
    {
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            return;
        }

        builder.Append(' ', depth * 2);
        builder.Append("- ");
        builder.Append(node.DisplayName);
        builder.Append(" [");
        builder.Append(node.Kind);
        builder.Append(']');
        if (node.Port is not null)
        {
            builder.Append($" chain={node.Port.PortChain} status={node.Port.ConnectionStatus} speed={node.Port.ConnectionSpeed}");
        }

        if (node.DeviceDescriptor is not null)
        {
            builder.Append($" vid:pid={node.DeviceDescriptor.VendorProduct} bcdUSB={node.DeviceDescriptor.UsbVersion}");
        }

        builder.AppendLine();
        foreach (var childId in node.ChildNodeIds)
        {
            AppendNode(builder, childId, nodes, depth + 1);
        }
    }
}

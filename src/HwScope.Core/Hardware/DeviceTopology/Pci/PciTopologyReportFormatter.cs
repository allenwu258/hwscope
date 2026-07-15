using System.Text;

namespace HwScope.Core.Hardware.DeviceTopology.Pci;

public static class PciTopologyReportFormatter
{
    public static string Format(PciTopologySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PCI Express Topology");
        builder.AppendLine($"Generated At: {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Nodes: {snapshot.Nodes.Count}, Roots: {snapshot.RootNodeIds.Count}, Diagnostics: {snapshot.Diagnostics.Entries.Count}");
        builder.AppendLine();

        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        foreach (var rootId in snapshot.RootNodeIds)
        {
            AppendNode(builder, nodes, rootId, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (snapshot.Diagnostics.Entries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            foreach (var entry in snapshot.Diagnostics.Entries)
            {
                builder.AppendLine($"- {entry.Severity} {entry.Code}: {entry.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendNode(
        StringBuilder builder,
        IReadOnlyDictionary<string, PciTopologyNode> nodes,
        string nodeId,
        int depth,
        ISet<string> visited)
    {
        if (!nodes.TryGetValue(nodeId, out var node) || !visited.Add(nodeId))
        {
            return;
        }

        var address = node.Address?.ToString() ?? "??:??.?";
        var link = node.Link.CurrentGeneration.IsAvailable || node.Link.CurrentWidth.IsAvailable
            ? $" · {string.Join(' ', new[]
            {
                node.Link.CurrentGeneration.IsAvailable ? node.Link.CurrentGeneration.DisplayText : null,
                node.Link.CurrentWidth.IsAvailable ? node.Link.CurrentWidth.DisplayText : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)))}"
            : string.Empty;
        var status = node.Identity.Status.HasProblem ? $" · Problem {node.Identity.Status.ProblemCode}" : string.Empty;
        builder.AppendLine($"{new string(' ', depth * 2)}- [{address}] {node.Identity.DisplayName} · {node.Class.DisplayName}{link}{status}");
        foreach (var childId in node.ChildNodeIds)
        {
            AppendNode(builder, nodes, childId, depth + 1, visited);
        }
    }
}

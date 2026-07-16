using System.Windows;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using Wpf.Ui.Controls;

namespace HwScope.App.Pages.DeviceTopology;

internal enum PciDiagnosticTreeFilter
{
    All,
    Problems,
    Bridges,
    Endpoints
}

internal sealed record PciDiagnosticTreeRow(
    string NodeId,
    int Depth,
    Thickness Indent,
    bool HasChildren,
    bool IsExpanded,
    string ExpansionGlyph,
    SymbolRegular Icon,
    string Label,
    string Coordinate,
    string KindLabel,
    string StatusText,
    bool HasProblem);

internal sealed class PciDiagnosticTreeProjection
{
    private readonly HashSet<string> _expandedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private PciTopologySnapshot _snapshot = PciTopologySnapshot.Empty;
    private IReadOnlyDictionary<string, PciTopologyNode> _nodes =
        new Dictionary<string, PciTopologyNode>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _diagnosticNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public void SetSnapshot(PciTopologySnapshot snapshot)
    {
        _snapshot = snapshot;
        _nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        _diagnosticNodeIds = snapshot.Diagnostics.Entries
            .Where(entry => entry.NodeId is not null
                && entry.Severity != DeviceTopologyDiagnosticSeverity.Information)
            .Select(entry => entry.NodeId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _expandedNodeIds.RemoveWhere(nodeId => !_nodes.ContainsKey(nodeId));
        if (_expandedNodeIds.Count == 0)
        {
            _expandedNodeIds.UnionWith(snapshot.RootNodeIds);
        }
    }

    public IReadOnlyList<PciDiagnosticTreeRow> Build(
        string? searchText,
        PciDiagnosticTreeFilter filter)
    {
        var search = searchText?.Trim() ?? string.Empty;
        var hasCriteria = search.Length > 0 || filter != PciDiagnosticTreeFilter.All;
        var included = hasCriteria
            ? BuildIncludedSet(search, filter)
            : _nodes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PciDiagnosticTreeRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootId in _snapshot.RootNodeIds)
        {
            AppendVisibleRows(rootId, 0, hasCriteria, included, visited, rows);
        }

        return rows;
    }

    public void Toggle(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node) || node.ChildNodeIds.Count == 0)
        {
            return;
        }

        if (!_expandedNodeIds.Remove(nodeId))
        {
            _expandedNodeIds.Add(nodeId);
        }
    }

    public void ExpandAll()
    {
        _expandedNodeIds.UnionWith(_snapshot.Nodes
            .Where(node => node.ChildNodeIds.Count > 0)
            .Select(node => node.NodeId));
    }

    public void CollapseAll()
    {
        _expandedNodeIds.Clear();
    }

    public bool ExpandPath(string nodeId)
    {
        if (!_nodes.ContainsKey(nodeId))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentId = nodeId;
        while (_nodes.TryGetValue(currentId, out var current) && visited.Add(currentId))
        {
            if (current.ParentNodeId is null)
            {
                break;
            }

            _expandedNodeIds.Add(current.ParentNodeId);
            currentId = current.ParentNodeId;
        }

        return true;
    }

    private HashSet<string> BuildIncludedSet(string search, PciDiagnosticTreeFilter filter)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _snapshot.Nodes)
        {
            if (!MatchesFilter(node, filter) || !MatchesSearch(node, search))
            {
                continue;
            }

            AddNodeAndAncestors(node.NodeId, included);
        }

        return included;
    }

    private void AddNodeAndAncestors(string nodeId, ISet<string> included)
    {
        var currentId = nodeId;
        while (_nodes.TryGetValue(currentId, out var current) && included.Add(currentId))
        {
            if (current.ParentNodeId is null)
            {
                break;
            }

            currentId = current.ParentNodeId;
        }
    }

    private void AppendVisibleRows(
        string nodeId,
        int depth,
        bool forceMatchingPathsOpen,
        IReadOnlySet<string> included,
        ISet<string> visited,
        ICollection<PciDiagnosticTreeRow> rows)
    {
        if (!included.Contains(nodeId)
            || !_nodes.TryGetValue(nodeId, out var node)
            || !visited.Add(nodeId))
        {
            return;
        }

        var visibleChildren = node.ChildNodeIds.Where(included.Contains).ToArray();
        var isExpanded = visibleChildren.Length > 0
            && (forceMatchingPathsOpen || _expandedNodeIds.Contains(nodeId));
        var hasProblem = node.Identity.Status.HasProblem || _diagnosticNodeIds.Contains(node.NodeId);
        rows.Add(new PciDiagnosticTreeRow(
            node.NodeId,
            depth,
            new Thickness(depth * 18, 0, 0, 0),
            visibleChildren.Length > 0,
            isExpanded,
            visibleChildren.Length == 0 ? string.Empty : isExpanded ? "−" : "+",
            FormatIcon(node.Kind),
            node.Identity.DisplayName,
            node.Address?.ToString() ?? "地址未报告",
            FormatKind(node.Kind),
            hasProblem ? FormatProblem(node) : "正常",
            hasProblem));

        if (!isExpanded)
        {
            return;
        }

        foreach (var childId in visibleChildren)
        {
            AppendVisibleRows(childId, depth + 1, forceMatchingPathsOpen, included, visited, rows);
        }
    }

    private bool MatchesFilter(PciTopologyNode node, PciDiagnosticTreeFilter filter)
    {
        return filter switch
        {
            PciDiagnosticTreeFilter.Problems =>
                node.Identity.Status.HasProblem || _diagnosticNodeIds.Contains(node.NodeId),
            PciDiagnosticTreeFilter.Bridges =>
                node.Kind is PciTopologyNodeKind.Root or PciTopologyNodeKind.Bridge,
            PciDiagnosticTreeFilter.Endpoints => node.Kind == PciTopologyNodeKind.Endpoint,
            _ => true
        };
    }

    private static bool MatchesSearch(PciTopologyNode node, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        return new[]
        {
            node.Identity.DisplayName,
            node.Identity.InstanceId,
            node.Address?.ToString(),
            node.PciIdentity.VendorId,
            node.PciIdentity.DeviceId,
            node.Class.DisplayName,
            node.Class.Code,
            node.Driver.Provider,
            node.Driver.Service
        }
        .Concat(node.Identity.HardwareIds)
        .Concat(node.Identity.LocationPaths)
        .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static SymbolRegular FormatIcon(PciTopologyNodeKind kind)
    {
        return kind switch
        {
            PciTopologyNodeKind.Root => SymbolRegular.Board20,
            PciTopologyNodeKind.Bridge => SymbolRegular.BranchFork24,
            _ => SymbolRegular.DeveloperBoard20
        };
    }

    private static string FormatKind(PciTopologyNodeKind kind)
    {
        return kind switch
        {
            PciTopologyNodeKind.Root => "Root",
            PciTopologyNodeKind.Bridge => "Bridge",
            PciTopologyNodeKind.Endpoint => "Endpoint",
            _ => "Unknown"
        };
    }

    private static string FormatProblem(PciTopologyNode node)
    {
        return node.Identity.Status.ProblemCode is > 0
            ? $"Problem {node.Identity.Status.ProblemCode}"
            : "诊断警告";
    }
}

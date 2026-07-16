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
    bool HasProblem,
    bool IsStandaloneDiagnostic = false);

internal sealed class PciDiagnosticTreeProjection
{
    private readonly HashSet<string> _expandedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _expansionInitialized;
    private PciTopologySnapshot _snapshot = PciTopologySnapshot.Empty;
    private IReadOnlyDictionary<string, PciTopologyNode> _nodes =
        new Dictionary<string, PciTopologyNode>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _diagnosticNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _searchTextByNodeId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, DeviceTopologyDiagnostic> _standaloneDiagnostics =
        new Dictionary<string, DeviceTopologyDiagnostic>(StringComparer.OrdinalIgnoreCase);

    public int ProblemCount { get; private set; }

    public int DiagnosticCount { get; private set; }

    public void SetSnapshot(
        PciTopologySnapshot snapshot,
        IReadOnlyList<DeviceTopologyDiagnostic>? diagnostics = null)
    {
        _snapshot = snapshot;
        _nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var effectiveDiagnostics = diagnostics ?? snapshot.Diagnostics.Entries;
        _diagnosticNodeIds = effectiveDiagnostics
            .Where(entry => entry.NodeId is not null
                && _nodes.ContainsKey(entry.NodeId)
                && entry.Severity != DeviceTopologyDiagnosticSeverity.Information)
            .Select(entry => entry.NodeId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _standaloneDiagnostics = effectiveDiagnostics
            .Where(entry => entry.NodeId is null || !_nodes.ContainsKey(entry.NodeId))
            .Select((entry, index) => new KeyValuePair<string, DeviceTopologyDiagnostic>(
                BuildDiagnosticRowId(entry, index),
                entry))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _searchTextByNodeId = snapshot.Nodes.ToDictionary(
            node => node.NodeId,
            BuildSearchText,
            StringComparer.OrdinalIgnoreCase);
        DiagnosticCount = effectiveDiagnostics.Count;
        ProblemCount = snapshot.Nodes.Count(node =>
                node.Identity.Status.HasProblem || _diagnosticNodeIds.Contains(node.NodeId))
            + _standaloneDiagnostics.Values.Count(diagnostic =>
                diagnostic.Severity != DeviceTopologyDiagnosticSeverity.Information);
        _expandedNodeIds.RemoveWhere(nodeId => !_nodes.ContainsKey(nodeId));
        if (!_expansionInitialized && snapshot.RootNodeIds.Count > 0)
        {
            _expandedNodeIds.UnionWith(snapshot.RootNodeIds);
            _expansionInitialized = true;
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

        AppendStandaloneDiagnostics(search, filter, rows);

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

    public bool TryGetParentNodeId(string nodeId, out string? parentNodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            parentNodeId = node.ParentNodeId;
            return parentNodeId is not null;
        }

        parentNodeId = null;
        return false;
    }

    public bool TryGetStandaloneDiagnostic(
        string rowId,
        out DeviceTopologyDiagnostic? diagnostic)
    {
        return _standaloneDiagnostics.TryGetValue(rowId, out diagnostic);
    }

    public bool IsSearchMatch(string rowId, string? searchText)
    {
        var search = searchText?.Trim() ?? string.Empty;
        if (search.Length == 0)
        {
            return true;
        }

        if (_standaloneDiagnostics.TryGetValue(rowId, out var diagnostic))
        {
            return BuildDiagnosticSearchText(diagnostic)
                .Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        return MatchesSearch(rowId, search);
    }

    private HashSet<string> BuildIncludedSet(string search, PciDiagnosticTreeFilter filter)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _snapshot.Nodes)
        {
            if (!MatchesFilter(node, filter) || !MatchesSearch(node.NodeId, search))
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

    private bool MatchesSearch(string nodeId, string search)
    {
        return search.Length == 0
            || (_searchTextByNodeId.TryGetValue(nodeId, out var searchText)
                && searchText.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void AppendStandaloneDiagnostics(
        string search,
        PciDiagnosticTreeFilter filter,
        ICollection<PciDiagnosticTreeRow> rows)
    {
        if (filter is PciDiagnosticTreeFilter.Bridges or PciDiagnosticTreeFilter.Endpoints)
        {
            return;
        }

        foreach (var (rowId, diagnostic) in _standaloneDiagnostics)
        {
            var isProblem = diagnostic.Severity != DeviceTopologyDiagnosticSeverity.Information;
            if (filter == PciDiagnosticTreeFilter.Problems && !isProblem)
            {
                continue;
            }

            if (search.Length > 0
                && !BuildDiagnosticSearchText(diagnostic).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new PciDiagnosticTreeRow(
                rowId,
                0,
                new Thickness(0),
                HasChildren: false,
                IsExpanded: false,
                ExpansionGlyph: string.Empty,
                SymbolRegular.Warning24,
                diagnostic.Message,
                diagnostic.Code,
                "Diagnostic",
                FormatSeverity(diagnostic.Severity),
                isProblem,
                IsStandaloneDiagnostic: true));
        }
    }

    private static string BuildSearchText(PciTopologyNode node)
    {
        return string.Join('\n', new string?[]
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
            .Where(value => !string.IsNullOrWhiteSpace(value))!);
    }

    private static string BuildDiagnosticSearchText(DeviceTopologyDiagnostic diagnostic)
    {
        return string.Join('\n', diagnostic.Code, diagnostic.Message, diagnostic.NodeId ?? string.Empty);
    }

    private static string BuildDiagnosticRowId(DeviceTopologyDiagnostic diagnostic, int index)
    {
        return $"pci-diagnostic:{index}:{diagnostic.Code}";
    }

    private static string FormatSeverity(DeviceTopologyDiagnosticSeverity severity)
    {
        return severity switch
        {
            DeviceTopologyDiagnosticSeverity.Error => "错误",
            DeviceTopologyDiagnosticSeverity.Warning => "警告",
            _ => "信息"
        };
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

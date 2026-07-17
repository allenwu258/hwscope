using System.Windows;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;
using Wpf.Ui.Controls;

namespace HwScope.App.Pages.DeviceTopology;

internal enum UsbDiagnosticTreeFilter
{
    All,
    Problems,
    Connected,
    EmptyPorts,
    ControllersAndHubs,
    Devices,
    Descriptors
}

internal enum UsbDiagnosticRowKind
{
    TopologyNode,
    Configuration,
    InterfaceAssociation,
    Interface,
    Endpoint,
    AdditionalDescriptor,
    Bos,
    BosCapability,
    StandaloneDiagnostic
}

internal sealed record UsbDescriptorSelection(
    string OwnerNodeId,
    UsbDiagnosticRowKind Kind,
    int ConfigurationIndex = -1,
    int ItemIndex = -1,
    int EndpointIndex = -1);

internal sealed record UsbDiagnosticTreeRow(
    string RowId,
    string? ParentRowId,
    string? TopologyNodeId,
    UsbDiagnosticRowKind RowKind,
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

internal sealed class UsbDiagnosticTreeProjection
{
    private readonly HashSet<string> _expandedRowIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsbDeviceDetailSnapshot> _detailsByNodeId =
        new(StringComparer.OrdinalIgnoreCase);
    private UsbTopologySnapshot _snapshot = UsbTopologySnapshot.Empty;
    private IReadOnlyDictionary<string, UsbTopologyNode> _nodes =
        new Dictionary<string, UsbTopologyNode>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DeviceTopologyDiagnostic> _diagnostics = [];
    private IReadOnlySet<string> _diagnosticNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, DeviceTopologyDiagnostic> _standaloneDiagnostics =
        new Dictionary<string, DeviceTopologyDiagnostic>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Entry> _lastEntries = new(StringComparer.OrdinalIgnoreCase);
    private bool _expansionInitialized;

    public int ProblemCount { get; private set; }

    public int DiagnosticCount => _diagnostics.Count
        + _detailsByNodeId.Values.Sum(detail => detail.Diagnostics.Entries.Count);

    public void SetSnapshot(
        UsbTopologySnapshot snapshot,
        IReadOnlyList<DeviceTopologyDiagnostic>? diagnostics = null)
    {
        _snapshot = snapshot;
        _nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        _diagnostics = diagnostics ?? snapshot.Diagnostics.Entries;
        _diagnosticNodeIds = _diagnostics
            .Where(entry => entry.NodeId is not null
                && _nodes.ContainsKey(entry.NodeId)
                && entry.Severity != DeviceTopologyDiagnosticSeverity.Information)
            .Select(entry => entry.NodeId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _standaloneDiagnostics = _diagnostics
            .Where(entry => entry.NodeId is null || !_nodes.ContainsKey(entry.NodeId))
            .Select((entry, index) => new KeyValuePair<string, DeviceTopologyDiagnostic>(
                $"usb-diagnostic:{index}:{entry.Code}", entry))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _detailsByNodeId.ToArray())
        {
            if (!_nodes.TryGetValue(entry.Key, out var node)
                || !string.Equals(node.AttachmentId, entry.Value.AttachmentId, StringComparison.OrdinalIgnoreCase))
            {
                _detailsByNodeId.Remove(entry.Key);
            }
        }

        ProblemCount = snapshot.Nodes.Count(HasProblem)
            + _standaloneDiagnostics.Values.Count(entry =>
                entry.Severity != DeviceTopologyDiagnosticSeverity.Information);
        if (!_expansionInitialized && snapshot.HostControllerNodeIds.Count > 0)
        {
            foreach (var controllerId in snapshot.HostControllerNodeIds)
            {
                AddDefaultExpansion(controllerId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            _expansionInitialized = true;
        }
    }

    public void SetDeviceDetail(UsbDeviceDetailSnapshot detail)
    {
        if (_nodes.TryGetValue(detail.DeviceNodeId, out var node)
            && string.Equals(node.AttachmentId, detail.AttachmentId, StringComparison.OrdinalIgnoreCase))
        {
            _detailsByNodeId[detail.DeviceNodeId] = detail;
            _expandedRowIds.Add(detail.DeviceNodeId);
            ProblemCount = _snapshot.Nodes.Count(HasProblem)
                + _standaloneDiagnostics.Values.Count(entry =>
                    entry.Severity != DeviceTopologyDiagnosticSeverity.Information);
        }
    }

    public UsbDeviceDetailSnapshot? TryGetDetail(string nodeId) =>
        _detailsByNodeId.GetValueOrDefault(nodeId);

    public IReadOnlyList<UsbDiagnosticTreeRow> Build(
        string? searchText,
        UsbDiagnosticTreeFilter filter)
    {
        var entries = BuildEntries();
        _lastEntries = entries;
        _expandedRowIds.RemoveWhere(rowId => !entries.ContainsKey(rowId));
        var search = searchText?.Trim() ?? string.Empty;
        var hasCriteria = search.Length > 0 || filter != UsbDiagnosticTreeFilter.All;
        var included = hasCriteria
            ? BuildIncludedSet(entries, search, filter)
            : entries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var children = entries.Values
            .Where(entry => entry.ParentRowId is not null)
            .GroupBy(entry => entry.ParentRowId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<UsbDiagnosticTreeRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controllerId in _snapshot.HostControllerNodeIds)
        {
            AppendRows(controllerId, 0, hasCriteria, included, entries, children, visited, rows);
        }

        foreach (var diagnosticId in _standaloneDiagnostics.Keys)
        {
            AppendRows(diagnosticId, 0, hasCriteria, included, entries, children, visited, rows);
        }

        return rows;
    }

    public void Toggle(string rowId)
    {
        if (!_lastEntries.TryGetValue(rowId, out var entry)
            || !_lastEntries.Values.Any(candidate =>
                string.Equals(candidate.ParentRowId, entry.RowId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!_expandedRowIds.Remove(rowId))
        {
            _expandedRowIds.Add(rowId);
        }
    }

    public void ExpandAll()
    {
        _lastEntries = BuildEntries();
        var parentIds = _lastEntries.Values
            .Where(entry => entry.ParentRowId is not null)
            .Select(entry => entry.ParentRowId!);
        _expandedRowIds.UnionWith(parentIds);
    }

    public void CollapseAll() => _expandedRowIds.Clear();

    public bool ExpandPath(string rowId)
    {
        var entries = BuildEntries();
        _lastEntries = entries;
        if (!entries.TryGetValue(rowId, out var current))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current.ParentRowId is not null && visited.Add(current.RowId))
        {
            _expandedRowIds.Add(current.ParentRowId);
            if (!entries.TryGetValue(current.ParentRowId, out current))
            {
                break;
            }
        }

        return true;
    }

    public bool TryGetParentRowId(string rowId, out string? parentRowId)
    {
        if (_lastEntries.TryGetValue(rowId, out var entry))
        {
            parentRowId = entry.ParentRowId;
            return parentRowId is not null;
        }

        parentRowId = null;
        return false;
    }

    public bool TryGetDescriptorSelection(string rowId, out UsbDescriptorSelection? selection)
    {
        if (_lastEntries.TryGetValue(rowId, out var entry) && entry.DescriptorSelection is not null)
        {
            selection = entry.DescriptorSelection;
            return true;
        }

        selection = null;
        return false;
    }

    public bool TryGetStandaloneDiagnostic(string rowId, out DeviceTopologyDiagnostic? diagnostic) =>
        _standaloneDiagnostics.TryGetValue(rowId, out diagnostic);

    public bool IsSearchMatch(string rowId, string? searchText)
    {
        var search = searchText?.Trim() ?? string.Empty;
        return search.Length == 0
            || (_lastEntries.TryGetValue(rowId, out var entry)
                && entry.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, Entry> BuildEntries()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _snapshot.Nodes)
        {
            entries[node.NodeId] = BuildTopologyEntry(node);
        }

        foreach (var (nodeId, detail) in _detailsByNodeId)
        {
            AddDescriptorEntries(entries, nodeId, detail);
        }

        foreach (var (rowId, diagnostic) in _standaloneDiagnostics)
        {
            entries[rowId] = new Entry(
                rowId, null, null, UsbDiagnosticRowKind.StandaloneDiagnostic,
                diagnostic.Message, diagnostic.Code, "Diagnostic",
                FormatSeverity(diagnostic.Severity),
                diagnostic.Severity != DeviceTopologyDiagnosticSeverity.Information,
                string.Join('\n', diagnostic.Code, diagnostic.Message, diagnostic.NodeId ?? string.Empty),
                null);
        }

        return entries;
    }

    private Entry BuildTopologyEntry(UsbTopologyNode node)
    {
        var hasProblem = HasProblem(node);
        var coordinate = node.Port is not null
            ? $"Port {node.Port.PortChain}"
            : node.DeviceDescriptor?.VendorProduct
                ?? (node.Hub is not null ? $"{node.Hub.PortCount} ports" : string.Empty);
        return new Entry(
            node.NodeId,
            node.ParentNodeId,
            node.NodeId,
            UsbDiagnosticRowKind.TopologyNode,
            node.DisplayName,
            coordinate,
            FormatKind(node.Kind),
            hasProblem ? FormatProblem(node) : FormatNormalStatus(node),
            hasProblem,
            string.Join('\n', new string?[]
                {
                    node.DisplayName,
                    node.NodeId,
                    node.DevicePath,
                    node.DriverKey,
                    node.Port?.PortChain,
                    node.Port?.ConnectionStatus.ToString(),
                    node.Port?.ConnectionSpeed.ToString(),
                    node.DeviceDescriptor?.VendorProduct,
                    node.Identity?.InstanceId,
                    node.Identity?.Manufacturer,
                    node.Identity?.Service
                }
                .Concat(node.Identity?.HardwareIds ?? [])
                .Concat(node.Identity?.LocationPaths ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))!),
            null);
    }

    private void AddDescriptorEntries(
        IDictionary<string, Entry> entries,
        string ownerNodeId,
        UsbDeviceDetailSnapshot detail)
    {
        for (var configurationIndex = 0; configurationIndex < detail.Configurations.Length; configurationIndex++)
        {
            var configuration = detail.Configurations[configurationIndex];
            var configurationId = $"{ownerNodeId}#configuration:{configurationIndex}";
            AddDescriptorEntry(entries, configurationId, ownerNodeId, ownerNodeId,
                UsbDiagnosticRowKind.Configuration,
                $"Configuration {configuration.DescriptorIndex}",
                $"value {configuration.ConfigurationValue}",
                $"{configuration.Interfaces.Length} interfaces",
                new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.Configuration, configurationIndex),
                configuration.Description);

            for (var iadIndex = 0; iadIndex < configuration.InterfaceAssociations.Length; iadIndex++)
            {
                var iad = configuration.InterfaceAssociations[iadIndex];
                AddDescriptorEntry(entries,
                    $"{configurationId}#iad:{iadIndex}", configurationId, ownerNodeId,
                    UsbDiagnosticRowKind.InterfaceAssociation,
                    $"IAD · Interface {iad.FirstInterface}-{iad.FirstInterface + iad.InterfaceCount - 1}",
                    $"class {iad.FunctionClass:X2}/{iad.FunctionSubClass:X2}/{iad.FunctionProtocol:X2}",
                    iad.Description ?? "Interface Association",
                    new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.InterfaceAssociation, configurationIndex, iadIndex),
                    iad.Description);
            }

            for (var interfaceIndex = 0; interfaceIndex < configuration.Interfaces.Length; interfaceIndex++)
            {
                var item = configuration.Interfaces[interfaceIndex];
                var interfaceId = $"{configurationId}#interface:{interfaceIndex}";
                AddDescriptorEntry(entries, interfaceId, configurationId, ownerNodeId,
                    UsbDiagnosticRowKind.Interface,
                    $"Interface {item.InterfaceNumber} · Alt {item.AlternateSetting}",
                    $"class {item.InterfaceClass:X2}/{item.InterfaceSubClass:X2}/{item.InterfaceProtocol:X2}",
                    $"{item.Endpoints.Length} endpoints",
                    new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.Interface, configurationIndex, interfaceIndex),
                    item.Description);

                for (var endpointIndex = 0; endpointIndex < item.Endpoints.Length; endpointIndex++)
                {
                    var endpoint = item.Endpoints[endpointIndex];
                    AddDescriptorEntry(entries,
                        $"{interfaceId}#endpoint:{endpointIndex}", interfaceId, ownerNodeId,
                        UsbDiagnosticRowKind.Endpoint,
                        $"Endpoint 0x{endpoint.Address:X2}",
                        $"{endpoint.Direction} · {endpoint.TransferType}",
                        $"{endpoint.MaximumPacketBytes} bytes",
                        new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.Endpoint,
                            configurationIndex, interfaceIndex, endpointIndex));
                }
            }

            for (var descriptorIndex = 0; descriptorIndex < configuration.AdditionalDescriptors.Length; descriptorIndex++)
            {
                var descriptor = configuration.AdditionalDescriptors[descriptorIndex];
                AddDescriptorEntry(entries,
                    $"{configurationId}#additional:{descriptorIndex}", configurationId, ownerNodeId,
                    UsbDiagnosticRowKind.AdditionalDescriptor,
                    $"Additional Descriptor 0x{descriptor.DescriptorType:X2}",
                    $"{descriptor.Length} bytes",
                    "Raw / class-specific",
                    new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.AdditionalDescriptor,
                        configurationIndex, descriptorIndex));
            }
        }

        if (detail.Bos is null)
        {
            return;
        }

        var bosId = $"{ownerNodeId}#bos";
        AddDescriptorEntry(entries, bosId, ownerNodeId, ownerNodeId,
            UsbDiagnosticRowKind.Bos, "BOS Descriptor", $"{detail.Bos.TotalLength} bytes",
            $"{detail.Bos.Capabilities.Length} capabilities",
            new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.Bos));
        for (var capabilityIndex = 0; capabilityIndex < detail.Bos.Capabilities.Length; capabilityIndex++)
        {
            var capability = detail.Bos.Capabilities[capabilityIndex];
            AddDescriptorEntry(entries,
                $"{bosId}#capability:{capabilityIndex}", bosId, ownerNodeId,
                UsbDiagnosticRowKind.BosCapability,
                capability.DisplayName,
                $"type 0x{capability.CapabilityType:X2}",
                $"{capability.RawBytes.Length} bytes",
                new UsbDescriptorSelection(ownerNodeId, UsbDiagnosticRowKind.BosCapability,
                    ItemIndex: capabilityIndex));
        }
    }

    private static void AddDescriptorEntry(
        IDictionary<string, Entry> entries,
        string rowId,
        string parentRowId,
        string ownerNodeId,
        UsbDiagnosticRowKind kind,
        string label,
        string coordinate,
        string status,
        UsbDescriptorSelection selection,
        string? description = null)
    {
        entries[rowId] = new Entry(
            rowId, parentRowId, ownerNodeId, kind, label, coordinate,
            FormatDescriptorKind(kind), status, false,
            string.Join('\n', label, coordinate, status, description ?? string.Empty),
            selection);
    }

    private HashSet<string> BuildIncludedSet(
        IReadOnlyDictionary<string, Entry> entries,
        string search,
        UsbDiagnosticTreeFilter filter)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            if (!MatchesFilter(entry, filter)
                || (search.Length > 0 && !entry.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var current = entry;
            while (included.Add(current.RowId)
                && current.ParentRowId is not null
                && entries.TryGetValue(current.ParentRowId, out current))
            {
            }
        }

        return included;
    }

    private bool MatchesFilter(Entry entry, UsbDiagnosticTreeFilter filter)
    {
        if (entry.RowKind == UsbDiagnosticRowKind.StandaloneDiagnostic)
        {
            return filter is UsbDiagnosticTreeFilter.All or UsbDiagnosticTreeFilter.Problems
                && (filter != UsbDiagnosticTreeFilter.Problems || entry.HasProblem);
        }

        if (entry.RowKind != UsbDiagnosticRowKind.TopologyNode)
        {
            return filter is UsbDiagnosticTreeFilter.All or UsbDiagnosticTreeFilter.Descriptors;
        }

        var node = _nodes[entry.TopologyNodeId!];
        return filter switch
        {
            UsbDiagnosticTreeFilter.All => true,
            UsbDiagnosticTreeFilter.Problems => entry.HasProblem,
            UsbDiagnosticTreeFilter.Connected => node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub
                || node.Port?.ConnectionStatus is not null and not UsbConnectionStatus.NoDeviceConnected,
            UsbDiagnosticTreeFilter.EmptyPorts => node.Kind == UsbTopologyNodeKind.Port
                && node.Port?.ConnectionStatus == UsbConnectionStatus.NoDeviceConnected,
            UsbDiagnosticTreeFilter.ControllersAndHubs => node.Kind is UsbTopologyNodeKind.HostController
                or UsbTopologyNodeKind.RootHub or UsbTopologyNodeKind.Hub,
            UsbDiagnosticTreeFilter.Devices => node.Kind == UsbTopologyNodeKind.Device,
            UsbDiagnosticTreeFilter.Descriptors => false,
            _ => true
        };
    }

    private void AppendRows(
        string rowId,
        int depth,
        bool forceMatchingPathsOpen,
        IReadOnlySet<string> included,
        IReadOnlyDictionary<string, Entry> entries,
        IReadOnlyDictionary<string, Entry[]> children,
        ISet<string> visited,
        ICollection<UsbDiagnosticTreeRow> rows)
    {
        if (!included.Contains(rowId) || !entries.TryGetValue(rowId, out var entry) || !visited.Add(rowId))
        {
            return;
        }

        var visibleChildren = children.GetValueOrDefault(rowId, [])
            .Where(child => included.Contains(child.RowId))
            .ToArray();
        var isExpanded = visibleChildren.Length > 0
            && (forceMatchingPathsOpen || _expandedRowIds.Contains(rowId));
        rows.Add(new UsbDiagnosticTreeRow(
            entry.RowId,
            entry.ParentRowId,
            entry.TopologyNodeId,
            entry.RowKind,
            depth,
            new Thickness(depth * 18, 0, 0, 0),
            visibleChildren.Length > 0,
            isExpanded,
            visibleChildren.Length == 0 ? string.Empty : isExpanded ? "-" : "+",
            FormatIcon(entry.RowKind, entry.TopologyNodeId),
            entry.Label,
            entry.Coordinate,
            entry.KindLabel,
            entry.StatusText,
            entry.HasProblem));

        if (!isExpanded)
        {
            return;
        }

        foreach (var child in visibleChildren)
        {
            AppendRows(child.RowId, depth + 1, forceMatchingPathsOpen,
                included, entries, children, visited, rows);
        }
    }

    private bool HasProblem(UsbTopologyNode node)
    {
        return node.Identity?.Status.HasProblem == true
            || node.Port?.ConnectionStatus is not null
                and not UsbConnectionStatus.NoDeviceConnected
                and not UsbConnectionStatus.DeviceConnected
            || _diagnosticNodeIds.Contains(node.NodeId)
            || (_detailsByNodeId.TryGetValue(node.NodeId, out var detail)
                && detail.Diagnostics.Entries.Any(entry =>
                    entry.Severity != DeviceTopologyDiagnosticSeverity.Information));
    }

    private void AddDefaultExpansion(string nodeId, ISet<string> visited)
    {
        if (!_nodes.TryGetValue(nodeId, out var node) || !visited.Add(nodeId))
        {
            return;
        }

        if (node.Kind is UsbTopologyNodeKind.HostController or UsbTopologyNodeKind.RootHub
            || node.ChildNodeIds.Any(childId => HasConnectedDescendant(childId, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase))))
        {
            _expandedRowIds.Add(nodeId);
        }

        foreach (var childId in node.ChildNodeIds)
        {
            AddDefaultExpansion(childId, visited);
        }
    }

    private bool HasConnectedDescendant(string nodeId, ISet<string> visited)
    {
        if (!_nodes.TryGetValue(nodeId, out var node) || !visited.Add(nodeId))
        {
            return false;
        }

        return node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub
            || node.ChildNodeIds.Any(childId => HasConnectedDescendant(childId, visited));
    }

    private static SymbolRegular FormatIcon(UsbDiagnosticRowKind rowKind, string? topologyNodeId)
    {
        if (rowKind == UsbDiagnosticRowKind.StandaloneDiagnostic)
        {
            return SymbolRegular.Warning24;
        }

        if (rowKind != UsbDiagnosticRowKind.TopologyNode)
        {
            return rowKind is UsbDiagnosticRowKind.Configuration or UsbDiagnosticRowKind.Bos
                ? SymbolRegular.BranchFork24
                : SymbolRegular.DeveloperBoard20;
        }

        return topologyNodeId is null ? SymbolRegular.DeveloperBoard20 : SymbolRegular.Board20;
    }

    private static string FormatKind(UsbTopologyNodeKind kind) => kind switch
    {
        UsbTopologyNodeKind.HostController => "Host Controller",
        UsbTopologyNodeKind.RootHub => "Root Hub",
        UsbTopologyNodeKind.Port => "Physical Port",
        UsbTopologyNodeKind.Hub => "USB Hub",
        UsbTopologyNodeKind.Device => "USB Device",
        _ => "Error"
    };

    private static string FormatDescriptorKind(UsbDiagnosticRowKind kind) => kind switch
    {
        UsbDiagnosticRowKind.Configuration => "Configuration",
        UsbDiagnosticRowKind.InterfaceAssociation => "IAD",
        UsbDiagnosticRowKind.Interface => "Interface",
        UsbDiagnosticRowKind.Endpoint => "Endpoint",
        UsbDiagnosticRowKind.AdditionalDescriptor => "Raw Descriptor",
        UsbDiagnosticRowKind.Bos => "BOS",
        UsbDiagnosticRowKind.BosCapability => "Capability",
        _ => "Descriptor"
    };

    private static string FormatNormalStatus(UsbTopologyNode node) =>
        node.Port?.ConnectionStatus == UsbConnectionStatus.NoDeviceConnected ? "空闲" : "正常";

    private static string FormatProblem(UsbTopologyNode node) =>
        node.Identity?.Status.ProblemCode is { } code
            ? $"Problem {code}"
            : node.Port?.ConnectionStatus.ToString() ?? "诊断警告";

    private static string FormatSeverity(DeviceTopologyDiagnosticSeverity severity) => severity switch
    {
        DeviceTopologyDiagnosticSeverity.Error => "错误",
        DeviceTopologyDiagnosticSeverity.Warning => "警告",
        _ => "信息"
    };

    private sealed record Entry(
        string RowId,
        string? ParentRowId,
        string? TopologyNodeId,
        UsbDiagnosticRowKind RowKind,
        string Label,
        string Coordinate,
        string KindLabel,
        string StatusText,
        bool HasProblem,
        string SearchText,
        UsbDescriptorSelection? DescriptorSelection);
}

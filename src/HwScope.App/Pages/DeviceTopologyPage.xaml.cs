using System.IO;
using System.Windows;
using System.Windows.Controls;
using HwScope.App.Pages.DeviceTopology;
using HwScope.App.Topology.Controls;
using HwScope.App.Topology.Model;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using Microsoft.Win32;

namespace HwScope.App.Pages;

public partial class DeviceTopologyPage : UserControl
{
    private readonly HashSet<string> _expandedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manuallyCollapsedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private PciTopologySnapshot? _snapshot;
    private string? _selectedNodeId;
    private CancellationTokenSource? _loadCancellation;

    public DeviceTopologyPage()
    {
        InitializeComponent();
        Loaded += DeviceTopologyPage_Loaded;
        Unloaded += DeviceTopologyPage_Unloaded;
    }

    public event EventHandler<string>? StatusChanged;

    private async void DeviceTopologyPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loadCancellation = new CancellationTokenSource();
        App.DeviceTopologies.PciStateChanged += DeviceTopologies_PciStateChanged;
        try
        {
            SetStatus("正在枚举 PCI Express 设备...");
            var result = await App.DeviceTopologies.EnsurePciLoadedAsync(_loadCancellation.Token).ConfigureAwait(true);
            RenderRefreshResult(result);
            SetStatus(FormatRefreshStatus(result, "PCI Express 拓扑已加载。"));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DeviceTopologyPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.DeviceTopologies.PciStateChanged -= DeviceTopologies_PciStateChanged;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void DeviceTopologies_PciStateChanged(object? sender, PciTopologyRefreshResult result)
    {
        if (IsLoaded)
        {
            RenderRefreshResult(result);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在刷新 PCI Express 拓扑...");
            var result = await App.DeviceTopologies.RefreshPciAsync(_loadCancellation?.Token ?? CancellationToken.None).ConfigureAwait(true);
            RenderRefreshResult(result);
            SetStatus(FormatRefreshStatus(result, "PCI Express 拓扑已刷新。"));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        Clipboard.SetText(PciTopologyReportFormatter.Format(_snapshot));
        SetStatus("PCI Express 拓扑报告已复制到剪贴板。");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 PCI Express 拓扑报告",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-PCIe-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, PciTopologyReportFormatter.Format(_snapshot));
        SetStatus($"PCI Express 拓扑报告已保存：{dialog.FileName}");
    }

    private void PciTopologyMap_ItemSelected(object? sender, TopologyItemSelectedEventArgs e)
    {
        _selectedNodeId = e.ItemId;
        PciTopologyMap.SelectedItemId = e.ItemId;
        RenderSelection();
    }

    private void PciTopologyMap_ExpansionToggled(object? sender, TopologyExpansionToggledEventArgs e)
    {
        if (e.IsExpanded)
        {
            _expandedNodeIds.Add(e.ItemId);
            _manuallyCollapsedNodeIds.Remove(e.ItemId);
        }
        else
        {
            _expandedNodeIds.Remove(e.ItemId);
            _manuallyCollapsedNodeIds.Add(e.ItemId);
        }

        RenderMap();
    }

    private void RenderSnapshot(PciTopologySnapshot snapshot)
    {
        _snapshot = snapshot;
        var currentIds = snapshot.Nodes.Select(node => node.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _expandedNodeIds.RemoveWhere(nodeId => !currentIds.Contains(nodeId));
        _manuallyCollapsedNodeIds.RemoveWhere(nodeId => !currentIds.Contains(nodeId));
        _expandedNodeIds.UnionWith(PciCompactTopologyAdapter
            .CreateDefaultExpansion(snapshot)
            .Where(nodeId => !_manuallyCollapsedNodeIds.Contains(nodeId)));

        var selectedExists = _selectedNodeId is not null && currentIds.Contains(_selectedNodeId);
        if (!selectedExists)
        {
            _selectedNodeId = snapshot.Nodes.FirstOrDefault(node =>
                    node.Kind == PciTopologyNodeKind.Endpoint
                    && (node.ParentNodeId is not null || node.Class.BaseClass.HasValue || node.Identity.Status.HasProblem))?.NodeId
                ?? snapshot.Nodes.FirstOrDefault()?.NodeId;
        }

        var rootCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Root);
        var bridgeCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Bridge);
        var endpointCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Endpoint);
        var unknownCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Unknown);
        var deviceCount = snapshot.Nodes.Count(node => node.Identity.Enumerator == "PCI");
        var problemCount = snapshot.Nodes.Count(node => node.Identity.Status.HasProblem);
        SubtitleText.Text = $"{deviceCount} 个设备 · {rootCount} 个根 · {bridgeCount} 个桥 · {endpointCount} 个端点 · {unknownCount} 个未分类 · {problemCount} 个异常 · {snapshot.GeneratedAt:HH:mm:ss}";
        MapHintText.Text = snapshot.Diagnostics.Entries.Count == 0
            ? "选择节点查看详情"
            : $"{snapshot.Diagnostics.Entries.Count} 条诊断";
        RenderMap();
        RenderSelection();
    }

    private void RenderRefreshResult(PciTopologyRefreshResult result)
    {
        RenderSnapshot(result.Snapshot);
        if (result.IsStale)
        {
            MapHintText.Text = $"刷新失败，显示 {result.Snapshot.GeneratedAt:HH:mm:ss} 的结果";
        }
        else if (result.CollectionFailed)
        {
            MapHintText.Text = result.AttemptDiagnostics.Entries.FirstOrDefault()?.Message ?? "PCI Express 枚举失败";
        }
    }

    private static string FormatRefreshStatus(PciTopologyRefreshResult result, string success)
    {
        if (result.IsStale)
        {
            return $"PCI Express 刷新失败，保留 {result.Snapshot.GeneratedAt:HH:mm:ss} 的上次成功结果。";
        }

        if (result.CollectionFailed)
        {
            return result.AttemptDiagnostics.Entries.FirstOrDefault()?.Message ?? "PCI Express 枚举失败。";
        }

        return result.Snapshot.Diagnostics.HasErrors ? "PCI Express 枚举完成，但存在诊断错误。" : success;
    }

    private void RenderMap()
    {
        if (_snapshot is null)
        {
            PciTopologyMap.Document = TopologyDocument.Empty;
            return;
        }

        PciTopologyMap.Document = PciCompactTopologyAdapter.ToDocument(_snapshot, _expandedNodeIds);
        PciTopologyMap.SelectedItemId = _selectedNodeId;
    }

    private void RenderSelection()
    {
        var node = _snapshot?.Nodes.FirstOrDefault(candidate => candidate.NodeId == _selectedNodeId);
        if (node is null)
        {
            SelectedTitleText.Text = "请选择一个 PCI Express 节点";
            SelectedMetaText.Text = string.Empty;
            SelectedPathText.Text = string.Empty;
            DetailSectionsList.ItemsSource = null;
            return;
        }

        SelectedTitleText.Text = node.Identity.DisplayName;
        SelectedMetaText.Text = $"{node.Address?.ToString() ?? "地址未报告"} · {node.Class.DisplayName} · {(node.Identity.Status.HasProblem ? $"Problem {node.Identity.Status.ProblemCode}" : "正常")}";
        SelectedPathText.Text = BuildBreadcrumb(node);
        DetailSectionsList.ItemsSource = BuildSections(node);
    }

    private string BuildBreadcrumb(PciTopologyNode node)
    {
        if (_snapshot is null)
        {
            return string.Empty;
        }

        var byId = _snapshot.Nodes.ToDictionary(item => item.NodeId, StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();
        PciTopologyNode? current = node;
        while (current is not null)
        {
            labels.Add(current.Address?.ToString() ?? current.Identity.DisplayName);
            current = current.ParentNodeId is not null && byId.TryGetValue(current.ParentNodeId, out var parent) ? parent : null;
        }

        labels.Reverse();
        return string.Join(" > ", labels);
    }

    private static IReadOnlyList<TopologyDetailSectionView> BuildSections(PciTopologyNode node)
    {
        return
        [
            new("身份与位置",
            [
                new("节点类型", node.DeviceType.ToString()),
                new("BDF", node.Address?.ToString() ?? "未报告"),
                new("Vendor / Device", JoinIdentity(node.PciIdentity.VendorId, node.PciIdentity.DeviceId)),
                new("Subsystem", Empty(node.PciIdentity.SubsystemId)),
                new("Revision", Empty(node.PciIdentity.RevisionId)),
                new("Location Path", node.Identity.LocationPaths.FirstOrDefault() ?? "未报告"),
                new("Instance ID", node.Identity.InstanceId)
            ]),
            new("链路与能力",
            [
                new("当前速率", node.Link.CurrentGeneration.DisplayText),
                new("当前宽度", node.Link.CurrentWidth.DisplayText),
                new("最大速率", node.Link.MaximumGeneration.DisplayText),
                new("最大宽度", node.Link.MaximumWidth.DisplayText),
                new("当前 Payload", node.Link.CurrentPayloadBytes.DisplayText),
                new("最大 Payload", node.Link.MaximumPayloadBytes.DisplayText),
                new("AER", node.Capabilities.AerCapabilityPresent.DisplayText)
            ]),
            new("驱动与状态",
            [
                new("厂商", Empty(node.Identity.Manufacturer)),
                new("驱动提供方", Empty(node.Driver.Provider)),
                new("驱动版本", Empty(node.Driver.Version)),
                new("INF", Empty(node.Driver.InfPath)),
                new("Service", Empty(node.Driver.Service)),
                new("Problem Code", node.Identity.Status.ProblemCode?.ToString() ?? "无")
            ]),
            new("原始地址",
            [
                new("BusNumber", node.RawBusNumber?.ToString() ?? "未报告"),
                new("Address", node.RawDeviceAddress?.ToString() ?? "未报告"),
                new("Class Code", Empty(node.Class.Code)),
                new("Hardware ID", node.Identity.HardwareIds.FirstOrDefault() ?? "未报告"),
                new("Container ID", node.Identity.ContainerId?.ToString("D") ?? "未报告")
            ])
        ];
    }

    private static string JoinIdentity(string vendor, string device)
    {
        return string.IsNullOrWhiteSpace(vendor) && string.IsNullOrWhiteSpace(device)
            ? "未报告"
            : $"{Empty(vendor)} / {Empty(device)}";
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "未报告" : value;

    private void SetStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }
}

public sealed record TopologyDetailSectionView(string Title, IReadOnlyList<TopologyDetailFieldView> Rows);

public sealed record TopologyDetailFieldView(string Label, string Value);

using System.IO;
using System.Windows;
using System.Windows.Controls;
using HwScope.App.Pages.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace HwScope.App.Windows;

public partial class DeviceTopologyDiagnosticsWindow : FluentWindow
{
    private readonly PciDiagnosticTreeProjection _projection = new();
    private CancellationTokenSource? _loadCancellation;
    private PciTopologySnapshot? _snapshot;
    private string? _selectedNodeId;
    private string? _pendingTargetNodeId;

    public DeviceTopologyDiagnosticsWindow(string? targetNodeId = null)
    {
        _pendingTargetNodeId = targetNodeId;
        InitializeComponent();
        Loaded += DeviceTopologyDiagnosticsWindow_Loaded;
        Closed += DeviceTopologyDiagnosticsWindow_Closed;
    }

    public void SetTarget(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

        _pendingTargetNodeId = nodeId;
        if (IsLoaded && _snapshot is not null)
        {
            SearchTextBox.Clear();
            FilterComboBox.SelectedIndex = 0;
            LocateTarget(nodeId);
        }
    }

    private async void DeviceTopologyDiagnosticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.ThemeService.Attach(this);
        App.DeviceTopologies.PciStateChanged += DeviceTopologies_PciStateChanged;
        _loadCancellation = new CancellationTokenSource();
        Loaded -= DeviceTopologyDiagnosticsWindow_Loaded;
        try
        {
            StatusText.Text = "正在读取 PCI Express 精确拓扑...";
            var result = await App.DeviceTopologies
                .EnsurePciLoadedAsync(_loadCancellation.Token)
                .ConfigureAwait(true);
            ApplyRefreshResult(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DeviceTopologyDiagnosticsWindow_Closed(object? sender, EventArgs e)
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
            ApplyRefreshResult(result);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在刷新 PCI Express 精确拓扑...";
            var result = await App.DeviceTopologies
                .RefreshPciAsync(_loadCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            ApplyRefreshResult(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyRefreshResult(PciTopologyRefreshResult result)
    {
        var previousSelection = _selectedNodeId;
        _snapshot = result.Snapshot;
        _projection.SetSnapshot(result.Snapshot);
        UpdateSummary(result.Snapshot);

        var target = _pendingTargetNodeId;
        _pendingTargetNodeId = null;
        if (target is null || !result.Snapshot.Nodes.Any(node =>
                string.Equals(node.NodeId, target, StringComparison.OrdinalIgnoreCase)))
        {
            target = previousSelection is not null && result.Snapshot.Nodes.Any(node =>
                    string.Equals(node.NodeId, previousSelection, StringComparison.OrdinalIgnoreCase))
                ? previousSelection
                : result.Snapshot.Nodes.FirstOrDefault(node => node.Kind == PciTopologyNodeKind.Root)?.NodeId
                    ?? result.Snapshot.Nodes.FirstOrDefault()?.NodeId;
        }

        if (target is not null)
        {
            _projection.ExpandPath(target);
        }

        RebuildRows(target);
        StatusText.Text = result.IsStale
            ? $"刷新失败，正在显示 {result.Snapshot.GeneratedAt:HH:mm:ss} 的上次成功结果。"
            : result.CollectionFailed
                ? result.AttemptDiagnostics.Entries.FirstOrDefault()?.Message ?? "PCI Express 枚举失败。"
                : $"PCI Express 精确拓扑已加载：{result.Snapshot.GeneratedAt:HH:mm:ss}";
    }

    private void UpdateSummary(PciTopologySnapshot snapshot)
    {
        var rootCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Root);
        var bridgeCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Bridge);
        var endpointCount = snapshot.Nodes.Count(node => node.Kind == PciTopologyNodeKind.Endpoint);
        var problemCount = snapshot.Nodes.Count(node => node.Identity.Status.HasProblem);
        SummaryText.Text = $"{rootCount} roots · {bridgeCount} bridges · {endpointCount} endpoints · {problemCount} problems · {snapshot.GeneratedAt:HH:mm:ss}";
    }

    private void RebuildRows(string? preferredSelection = null)
    {
        if (_snapshot is null)
        {
            TreeRowsList.ItemsSource = null;
            VisibleCountText.Text = "0 个可见节点";
            return;
        }

        var rows = _projection.Build(SearchTextBox.Text, ReadFilter());
        TreeRowsList.ItemsSource = rows;
        VisibleCountText.Text = $"{rows.Count} / {_snapshot.Nodes.Count} 个节点";
        var selectionId = preferredSelection ?? _selectedNodeId;
        var selectedRow = selectionId is null
            ? null
            : rows.FirstOrDefault(row =>
                string.Equals(row.NodeId, selectionId, StringComparison.OrdinalIgnoreCase));
        TreeRowsList.SelectedItem = selectedRow;
        if (selectedRow is not null)
        {
            TreeRowsList.ScrollIntoView(selectedRow);
            RenderNode(selectedRow.NodeId);
        }
        else if (rows.Count > 0)
        {
            TreeRowsList.SelectedIndex = 0;
        }
        else
        {
            ClearNodeDetails();
        }
    }

    private void LocateTarget(string nodeId)
    {
        if (!_projection.ExpandPath(nodeId))
        {
            StatusText.Text = $"未能在当前 snapshot 中定位节点：{nodeId}";
            return;
        }

        RebuildRows(nodeId);
        StatusText.Text = "已定位主页面传入的 PCI Express 节点。";
    }

    private void TreeRowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TreeRowsList.SelectedItem is PciDiagnosticTreeRow row)
        {
            RenderNode(row.NodeId);
        }
    }

    private void TreeExpansionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string nodeId })
        {
            _projection.Toggle(nodeId);
            RebuildRows(_selectedNodeId);
            e.Handled = true;
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RebuildRows();
        }
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RebuildRows();
        }
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        _projection.ExpandAll();
        RebuildRows(_selectedNodeId);
    }

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        _projection.CollapseAll();
        RebuildRows(_selectedNodeId);
    }

    private void RenderNode(string nodeId)
    {
        var node = _snapshot?.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        if (node is null || _snapshot is null)
        {
            ClearNodeDetails();
            return;
        }

        _selectedNodeId = node.NodeId;
        SelectedNodeTitleText.Text = node.Identity.DisplayName;
        SelectedNodeMetaText.Text = $"{node.Address?.ToString() ?? "地址未报告"} · {node.Kind} · {node.DeviceType} · {(node.Identity.Status.HasProblem ? $"Problem {node.Identity.Status.ProblemCode}" : "正常")}";
        OverviewFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildOverview(node, _snapshot);
        LinkFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildLinkAndCapabilities(node);
        ResourceFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildResources(node);
        DriverFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildDriver(node);
        RawReportTextBox.Text = PciDiagnosticNodeFormatter.BuildRawReport(node, _snapshot);
        OverviewScrollViewer.ScrollToTop();
        LinkScrollViewer.ScrollToTop();
        ResourceScrollViewer.ScrollToTop();
        DriverScrollViewer.ScrollToTop();
        RawReportTextBox.ScrollToHome();
    }

    private void ClearNodeDetails()
    {
        _selectedNodeId = null;
        SelectedNodeTitleText.Text = "没有匹配的 PCI Express 节点";
        SelectedNodeMetaText.Text = string.Empty;
        OverviewFieldsList.ItemsSource = null;
        LinkFieldsList.ItemsSource = null;
        ResourceFieldsList.ItemsSource = null;
        DriverFieldsList.ItemsSource = null;
        RawReportTextBox.Clear();
    }

    private void CopyNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RawReportTextBox.Text))
        {
            Clipboard.SetText(RawReportTextBox.Text);
            StatusText.Text = "当前 PCI Express 节点诊断已复制。";
        }
    }

    private void SaveNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RawReportTextBox.Text))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 PCI Express 节点诊断",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-PCIe-Node-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, RawReportTextBox.Text);
        StatusText.Text = $"节点诊断已保存：{dialog.FileName}";
    }

    private PciDiagnosticTreeFilter ReadFilter()
    {
        var tag = (FilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<PciDiagnosticTreeFilter>(tag, out var filter)
            ? filter
            : PciDiagnosticTreeFilter.All;
    }
}

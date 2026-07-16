using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HwScope.App.Pages.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace HwScope.App.Windows;

public partial class DeviceTopologyDiagnosticsWindow : FluentWindow
{
    private readonly PciDiagnosticTreeProjection _projection = new();
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _loadCancellation;
    private PciTopologySnapshot? _snapshot;
    private IReadOnlyList<DeviceTopologyDiagnostic> _diagnostics = [];
    private PciTopologyRefreshResult? _lastAppliedResult;
    private string? _selectedNodeId;
    private string? _pendingTargetNodeId;
    private bool _isRebuildingRows;
    private int _detailRenderVersion;

    public DeviceTopologyDiagnosticsWindow(string? targetNodeId = null)
    {
        _pendingTargetNodeId = targetNodeId;
        InitializeComponent();
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
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
            _searchDebounceTimer.Stop();
            SearchTextBox.Clear();
            FilterComboBox.SelectedIndex = 0;
            _searchDebounceTimer.Stop();
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
        _searchDebounceTimer.Stop();
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
        if (ReferenceEquals(_lastAppliedResult, result))
        {
            return;
        }

        _lastAppliedResult = result;
        var previousSelection = _selectedNodeId;
        _snapshot = result.Snapshot;
        _diagnostics = MergeDiagnostics(result.Snapshot.Diagnostics.Entries, result.AttemptDiagnostics.Entries);
        _projection.SetSnapshot(result.Snapshot, _diagnostics);
        UpdateSummary(result.Snapshot);

        var target = _pendingTargetNodeId;
        _pendingTargetNodeId = null;
        if (target is not null && !result.Snapshot.Nodes.Any(node =>
                string.Equals(node.NodeId, target, StringComparison.OrdinalIgnoreCase)))
        {
            target = null;
        }

        target ??= previousSelection
            ?? result.Snapshot.Nodes.FirstOrDefault(node => node.Kind == PciTopologyNodeKind.Root)?.NodeId
            ?? result.Snapshot.Nodes.FirstOrDefault()?.NodeId;

        if (target is not null && result.Snapshot.Nodes.Any(node =>
                string.Equals(node.NodeId, target, StringComparison.OrdinalIgnoreCase)))
        {
            _projection.ExpandPath(target);
        }

        RebuildRows(
            target,
            resetDetailScroll: !string.Equals(target, previousSelection, StringComparison.OrdinalIgnoreCase));
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
        SummaryText.Text = $"{rootCount} roots · {bridgeCount} bridges · {endpointCount} endpoints · {_projection.ProblemCount} problems · {_projection.DiagnosticCount} diagnostics · {snapshot.GeneratedAt:HH:mm:ss}";
    }

    private void RebuildRows(string? preferredSelection = null, bool resetDetailScroll = false)
    {
        if (_snapshot is null)
        {
            TreeRowsList.ItemsSource = null;
            VisibleCountText.Text = "0 个可见节点";
            return;
        }

        var rows = _projection.Build(SearchTextBox.Text, ReadFilter());
        VisibleCountText.Text = $"{rows.Count} 个可见条目 · {_snapshot.Nodes.Count} 个节点";
        var selectionId = preferredSelection ?? _selectedNodeId;
        var selectedRow = selectionId is null
            ? null
            : rows.FirstOrDefault(row =>
                string.Equals(row.NodeId, selectionId, StringComparison.OrdinalIgnoreCase));
        selectedRow ??= rows.FirstOrDefault();

        _isRebuildingRows = true;
        try
        {
            TreeRowsList.ItemsSource = rows;
            TreeRowsList.SelectedItem = selectedRow;
        }
        finally
        {
            _isRebuildingRows = false;
        }

        if (selectedRow is not null)
        {
            TreeRowsList.ScrollIntoView(selectedRow);
            var selectionChanged = !string.Equals(
                selectedRow.NodeId,
                _selectedNodeId,
                StringComparison.OrdinalIgnoreCase);
            RenderRow(selectedRow.NodeId, resetDetailScroll || selectionChanged);
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
        if (!_isRebuildingRows && TreeRowsList.SelectedItem is PciDiagnosticTreeRow row)
        {
            RenderRow(row.NodeId, resetDetailScroll: true);
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

    private void TreeRowsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TreeRowsList.SelectedItem is not PciDiagnosticTreeRow row
            || row.IsStandaloneDiagnostic)
        {
            return;
        }

        if (e.Key == Key.Right)
        {
            if (row.HasChildren && !row.IsExpanded)
            {
                _projection.Toggle(row.NodeId);
                RebuildRows(row.NodeId);
            }
            else
            {
                SelectFirstVisibleChild(row);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (row.IsExpanded && !HasActiveTreeCriteria())
            {
                _projection.Toggle(row.NodeId);
                RebuildRows(row.NodeId);
            }
            else if (_projection.TryGetParentNodeId(row.NodeId, out var parentNodeId)
                && parentNodeId is not null)
            {
                SelectVisibleRow(parentNodeId);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RenderRow(row.NodeId, resetDetailScroll: true);
            e.Handled = true;
        }
    }

    private void DiagnosticsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F3 || string.IsNullOrWhiteSpace(SearchTextBox.Text))
        {
            return;
        }

        MoveSearchMatch(backward: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void MoveSearchMatch(bool backward)
    {
        if (TreeRowsList.ItemsSource is not IEnumerable<PciDiagnosticTreeRow> source)
        {
            return;
        }

        var matches = source
            .Where(row => _projection.IsSearchMatch(row.NodeId, SearchTextBox.Text))
            .ToList();
        if (matches.Count == 0)
        {
            return;
        }

        var currentIndex = TreeRowsList.SelectedItem is PciDiagnosticTreeRow selected
            ? matches.FindIndex(row => string.Equals(
                row.NodeId,
                selected.NodeId,
                StringComparison.OrdinalIgnoreCase))
            : -1;
        var nextIndex = backward
            ? currentIndex <= 0 ? matches.Count - 1 : currentIndex - 1
            : currentIndex < 0 || currentIndex == matches.Count - 1 ? 0 : currentIndex + 1;
        SelectVisibleRow(matches[nextIndex].NodeId);
    }

    private void SelectVisibleRow(string rowId)
    {
        if (TreeRowsList.ItemsSource is not IEnumerable<PciDiagnosticTreeRow> source)
        {
            return;
        }

        var row = source.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeId, rowId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        TreeRowsList.SelectedItem = row;
        TreeRowsList.ScrollIntoView(row);
        TreeRowsList.Focus();
    }

    private void SelectFirstVisibleChild(PciDiagnosticTreeRow parent)
    {
        if (TreeRowsList.ItemsSource is not IReadOnlyList<PciDiagnosticTreeRow> rows)
        {
            return;
        }

        var parentIndex = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], parent)
                || string.Equals(rows[index].NodeId, parent.NodeId, StringComparison.OrdinalIgnoreCase))
            {
                parentIndex = index;
                break;
            }
        }

        if (parentIndex >= 0
            && parentIndex + 1 < rows.Count
            && rows[parentIndex + 1].Depth == parent.Depth + 1)
        {
            SelectVisibleRow(rows[parentIndex + 1].NodeId);
        }
    }

    private bool HasActiveTreeCriteria() =>
        !string.IsNullOrWhiteSpace(SearchTextBox.Text)
        || ReadFilter() != PciDiagnosticTreeFilter.All;

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        RebuildRows();
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
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

    private void RenderRow(string rowId, bool resetDetailScroll)
    {
        var node = _snapshot?.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeId, rowId, StringComparison.OrdinalIgnoreCase));
        if (node is not null && _snapshot is not null)
        {
            RenderNode(node, resetDetailScroll);
            return;
        }

        if (_snapshot is not null
            && _projection.TryGetStandaloneDiagnostic(rowId, out var diagnostic)
            && diagnostic is not null)
        {
            RenderDiagnostic(rowId, diagnostic, resetDetailScroll);
            return;
        }

        ClearNodeDetails();
    }

    private void RenderNode(PciTopologyNode node, bool resetDetailScroll)
    {
        if (_snapshot is null)
        {
            ClearNodeDetails();
            return;
        }

        var scrollState = resetDetailScroll ? null : CaptureDetailScrollState();
        var renderVersion = ++_detailRenderVersion;
        _selectedNodeId = node.NodeId;
        SelectedNodeTitleText.Text = node.Identity.DisplayName;
        SelectedNodeMetaText.Text = $"{node.Address?.ToString() ?? "地址未报告"} · {node.Kind} · {node.DeviceType} · {(node.Identity.Status.HasProblem ? $"Problem {node.Identity.Status.ProblemCode}" : "正常")}";
        OverviewFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildOverview(node, _snapshot);
        LinkFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildLinkAndCapabilities(node);
        ResourceFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildResources(node);
        DriverFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildDriver(node);
        RawReportTextBox.Text = PciDiagnosticNodeFormatter.BuildRawReport(node, _snapshot, _diagnostics);
        CompleteDetailRender(resetDetailScroll, scrollState, renderVersion);
    }

    private void RenderDiagnostic(
        string rowId,
        DeviceTopologyDiagnostic diagnostic,
        bool resetDetailScroll)
    {
        if (_snapshot is null)
        {
            ClearNodeDetails();
            return;
        }

        var scrollState = resetDetailScroll ? null : CaptureDetailScrollState();
        var renderVersion = ++_detailRenderVersion;
        _selectedNodeId = rowId;
        SelectedNodeTitleText.Text = diagnostic.Code;
        SelectedNodeMetaText.Text = $"{diagnostic.Severity} · {diagnostic.NodeId ?? "未归属节点"}";
        OverviewFieldsList.ItemsSource = PciDiagnosticNodeFormatter.BuildDiagnosticOverview(diagnostic, _snapshot);
        LinkFieldsList.ItemsSource = null;
        ResourceFieldsList.ItemsSource = null;
        DriverFieldsList.ItemsSource = null;
        RawReportTextBox.Text = PciDiagnosticNodeFormatter.BuildDiagnosticReport(diagnostic, _snapshot);
        CompleteDetailRender(resetDetailScroll, scrollState, renderVersion);
    }

    private void CompleteDetailRender(
        bool resetDetailScroll,
        DetailScrollState? scrollState,
        int renderVersion)
    {
        if (resetDetailScroll || scrollState is null)
        {
            OverviewScrollViewer.ScrollToTop();
            LinkScrollViewer.ScrollToTop();
            ResourceScrollViewer.ScrollToTop();
            DriverScrollViewer.ScrollToTop();
            RawReportTextBox.ScrollToHome();
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                if (renderVersion == _detailRenderVersion)
                {
                    RestoreDetailScrollState(scrollState);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void ClearNodeDetails()
    {
        _detailRenderVersion++;
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

    private void CopyFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: PciDiagnosticFieldView field
            }
            && !string.IsNullOrWhiteSpace(field.Value))
        {
            Clipboard.SetText(field.Value);
            StatusText.Text = $"已复制字段：{field.Label}";
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

    private static IReadOnlyList<DeviceTopologyDiagnostic> MergeDiagnostics(
        IReadOnlyList<DeviceTopologyDiagnostic> snapshotDiagnostics,
        IReadOnlyList<DeviceTopologyDiagnostic> attemptDiagnostics)
    {
        return snapshotDiagnostics
            .Concat(attemptDiagnostics)
            .Distinct()
            .ToArray();
    }

    private DetailScrollState CaptureDetailScrollState()
    {
        var rawScrollViewer = FindVisualChild<ScrollViewer>(RawReportTextBox);
        return new DetailScrollState(
            OverviewScrollViewer.VerticalOffset,
            LinkScrollViewer.VerticalOffset,
            ResourceScrollViewer.VerticalOffset,
            DriverScrollViewer.VerticalOffset,
            rawScrollViewer?.HorizontalOffset ?? 0,
            rawScrollViewer?.VerticalOffset ?? 0);
    }

    private void RestoreDetailScrollState(DetailScrollState state)
    {
        OverviewScrollViewer.ScrollToVerticalOffset(state.OverviewOffset);
        LinkScrollViewer.ScrollToVerticalOffset(state.LinkOffset);
        ResourceScrollViewer.ScrollToVerticalOffset(state.ResourceOffset);
        DriverScrollViewer.ScrollToVerticalOffset(state.DriverOffset);
        var rawScrollViewer = FindVisualChild<ScrollViewer>(RawReportTextBox);
        rawScrollViewer?.ScrollToHorizontalOffset(state.RawHorizontalOffset);
        rawScrollViewer?.ScrollToVerticalOffset(state.RawVerticalOffset);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private PciDiagnosticTreeFilter ReadFilter()
    {
        var tag = (FilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<PciDiagnosticTreeFilter>(tag, out var filter)
            ? filter
            : PciDiagnosticTreeFilter.All;
    }

    private sealed record DetailScrollState(
        double OverviewOffset,
        double LinkOffset,
        double ResourceOffset,
        double DriverOffset,
        double RawHorizontalOffset,
        double RawVerticalOffset);
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HwScope.App.Pages.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace HwScope.App.Windows;

public partial class UsbTopologyDiagnosticsWindow : FluentWindow
{
    private readonly UsbDiagnosticTreeProjection _projection = new();
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _detailLoadCancellation;
    private UsbTopologySnapshot? _snapshot;
    private IReadOnlyList<DeviceTopologyDiagnostic> _diagnostics = [];
    private UsbTopologyRefreshResult? _lastAppliedResult;
    private string? _selectedRowId;
    private string? _selectedTopologyNodeId;
    private string? _pendingTargetNodeId;
    private bool _isRebuildingRows;
    private int _detailLoadVersion;
    private int _detailRenderVersion;

    public UsbTopologyDiagnosticsWindow(string? targetNodeId = null)
    {
        _pendingTargetNodeId = targetNodeId;
        InitializeComponent();
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        Loaded += UsbTopologyDiagnosticsWindow_Loaded;
        Closed += UsbTopologyDiagnosticsWindow_Closed;
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

    private async void UsbTopologyDiagnosticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.ThemeService.Attach(this);
        App.DeviceTopologies.UsbStateChanged += DeviceTopologies_UsbStateChanged;
        _loadCancellation = new CancellationTokenSource();
        Loaded -= UsbTopologyDiagnosticsWindow_Loaded;
        try
        {
            StatusText.Text = "正在读取 USB 物理端口精确拓扑...";
            var result = await App.DeviceTopologies
                .EnsureUsbLoadedAsync(_loadCancellation.Token)
                .ConfigureAwait(true);
            ApplyRefreshResult(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UsbTopologyDiagnosticsWindow_Closed(object? sender, EventArgs e)
    {
        App.DeviceTopologies.UsbStateChanged -= DeviceTopologies_UsbStateChanged;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        CancelDetailLoad();
        _searchDebounceTimer.Stop();
    }

    private void DeviceTopologies_UsbStateChanged(object? sender, UsbTopologyRefreshResult result)
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
            CancelDetailLoad();
            StatusText.Text = "正在刷新 USB 物理端口精确拓扑...";
            var result = await App.DeviceTopologies
                .RefreshUsbAsync(_loadCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            ApplyRefreshResult(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void RefreshDescriptorButton_Click(object sender, RoutedEventArgs e)
    {
        var nodeId = ResolveSelectedDescriptorOwner();
        if (_snapshot is null || nodeId is null
            || UsbDeviceDetailTarget.FromSnapshot(_snapshot, nodeId) is null)
        {
            StatusText.Text = "当前条目没有可刷新的深层 USB descriptor。";
            return;
        }

        await LoadDeviceDetailAsync(nodeId, forceRefresh: true).ConfigureAwait(true);
    }

    private void ApplyRefreshResult(UsbTopologyRefreshResult result)
    {
        if (ReferenceEquals(_lastAppliedResult, result))
        {
            return;
        }

        _lastAppliedResult = result;
        var previousSelection = _selectedRowId;
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
            ?? result.Snapshot.HostControllerNodeIds.FirstOrDefault()
            ?? result.Snapshot.Nodes.FirstOrDefault()?.NodeId;
        if (target is not null)
        {
            _projection.ExpandPath(target);
        }

        RebuildRows(target, resetDetailScroll:
            !string.Equals(target, previousSelection, StringComparison.OrdinalIgnoreCase));
        StatusText.Text = result.IsStale
            ? $"刷新失败，正在显示 {result.Snapshot.GeneratedAt:HH:mm:ss} 的上次成功结果。"
            : result.CollectionFailed
                ? result.AttemptDiagnostics.Entries.FirstOrDefault()?.Message ?? "USB 枚举失败。"
                : $"USB 精确拓扑已加载：{result.Snapshot.GeneratedAt:HH:mm:ss}";
    }

    private void UpdateSummary(UsbTopologySnapshot snapshot)
    {
        var controllerCount = snapshot.Nodes.Count(node => node.Kind == UsbTopologyNodeKind.HostController);
        var portCount = snapshot.Nodes.Count(node => node.Kind == UsbTopologyNodeKind.Port);
        var connectedCount = snapshot.Nodes.Count(node =>
            node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub);
        SummaryText.Text = $"{controllerCount} controllers · {portCount} physical ports · {connectedCount} attached devices/hubs · {_projection.ProblemCount} problems · {_projection.DiagnosticCount} diagnostics · {snapshot.GeneratedAt:HH:mm:ss}";
    }

    private void RebuildRows(string? preferredSelection = null, bool resetDetailScroll = false)
    {
        if (_snapshot is null)
        {
            TreeRowsList.ItemsSource = null;
            VisibleCountText.Text = "0 个可见条目";
            return;
        }

        var rows = _projection.Build(SearchTextBox.Text, ReadFilter());
        VisibleCountText.Text = $"{rows.Count} 个可见条目 · {_snapshot.Nodes.Count} 个物理节点";
        var selectionId = preferredSelection ?? _selectedRowId;
        var selectedRow = selectionId is null
            ? null
            : rows.FirstOrDefault(row =>
                string.Equals(row.RowId, selectionId, StringComparison.OrdinalIgnoreCase));
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
                selectedRow.RowId, _selectedRowId, StringComparison.OrdinalIgnoreCase);
            RenderRow(selectedRow, resetDetailScroll || selectionChanged);
        }
        else
        {
            ClearDetails();
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
        StatusText.Text = "已定位主页面传入的 USB 节点。";
    }

    private void TreeRowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isRebuildingRows && TreeRowsList.SelectedItem is UsbDiagnosticTreeRow row)
        {
            RenderRow(row, resetDetailScroll: true);
        }
    }

    private void TreeExpansionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string rowId })
        {
            _projection.Toggle(rowId);
            RebuildRows(_selectedRowId);
            e.Handled = true;
        }
    }

    private void TreeRowsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TreeRowsList.SelectedItem is not UsbDiagnosticTreeRow row)
        {
            return;
        }

        if (e.Key == Key.Right)
        {
            if (row.HasChildren && !row.IsExpanded)
            {
                _projection.Toggle(row.RowId);
                RebuildRows(row.RowId);
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
                _projection.Toggle(row.RowId);
                RebuildRows(row.RowId);
            }
            else if (_projection.TryGetParentRowId(row.RowId, out var parentRowId)
                && parentRowId is not null)
            {
                SelectVisibleRow(parentRowId);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RenderRow(row, resetDetailScroll: true);
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
        if (TreeRowsList.ItemsSource is not IEnumerable<UsbDiagnosticTreeRow> source)
        {
            return;
        }

        var matches = source
            .Where(row => _projection.IsSearchMatch(row.RowId, SearchTextBox.Text))
            .ToList();
        if (matches.Count == 0)
        {
            return;
        }

        var currentIndex = TreeRowsList.SelectedItem is UsbDiagnosticTreeRow selected
            ? matches.FindIndex(row => string.Equals(
                row.RowId, selected.RowId, StringComparison.OrdinalIgnoreCase))
            : -1;
        var nextIndex = backward
            ? currentIndex <= 0 ? matches.Count - 1 : currentIndex - 1
            : currentIndex < 0 || currentIndex == matches.Count - 1 ? 0 : currentIndex + 1;
        SelectVisibleRow(matches[nextIndex].RowId);
    }

    private void SelectVisibleRow(string rowId)
    {
        if (TreeRowsList.ItemsSource is not IEnumerable<UsbDiagnosticTreeRow> source)
        {
            return;
        }

        var row = source.FirstOrDefault(candidate =>
            string.Equals(candidate.RowId, rowId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        TreeRowsList.SelectedItem = row;
        TreeRowsList.ScrollIntoView(row);
        TreeRowsList.Focus();
    }

    private void SelectFirstVisibleChild(UsbDiagnosticTreeRow parent)
    {
        if (TreeRowsList.ItemsSource is not IReadOnlyList<UsbDiagnosticTreeRow> rows)
        {
            return;
        }

        var parentIndex = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (string.Equals(rows[index].RowId, parent.RowId, StringComparison.OrdinalIgnoreCase))
            {
                parentIndex = index;
                break;
            }
        }

        if (parentIndex >= 0 && parentIndex + 1 < rows.Count
            && rows[parentIndex + 1].Depth == parent.Depth + 1)
        {
            SelectVisibleRow(rows[parentIndex + 1].RowId);
        }
    }

    private bool HasActiveTreeCriteria() =>
        !string.IsNullOrWhiteSpace(SearchTextBox.Text)
        || ReadFilter() != UsbDiagnosticTreeFilter.All;

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
        RebuildRows(_selectedRowId);
    }

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        _projection.CollapseAll();
        RebuildRows(_selectedRowId);
    }

    private void RenderRow(UsbDiagnosticTreeRow row, bool resetDetailScroll)
    {
        CancelDetailLoad();
        _selectedRowId = row.RowId;
        if (_snapshot is null)
        {
            ClearDetails();
            return;
        }

        if (row.RowKind == UsbDiagnosticRowKind.TopologyNode
            && row.TopologyNodeId is not null
            && _snapshot.Nodes.FirstOrDefault(node =>
                string.Equals(node.NodeId, row.TopologyNodeId, StringComparison.OrdinalIgnoreCase)) is { } node)
        {
            RenderTopologyNode(node, resetDetailScroll);
            return;
        }

        if (_projection.TryGetDescriptorSelection(row.RowId, out var selection)
            && selection is not null
            && _projection.TryGetDetail(selection.OwnerNodeId) is { } detail)
        {
            RenderDescriptor(detail, selection, resetDetailScroll);
            return;
        }

        if (_projection.TryGetStandaloneDiagnostic(row.RowId, out var diagnostic)
            && diagnostic is not null)
        {
            RenderDiagnostic(diagnostic, resetDetailScroll);
            return;
        }

        ClearDetails();
    }

    private void RenderTopologyNode(UsbTopologyNode node, bool resetDetailScroll)
    {
        if (_snapshot is null)
        {
            return;
        }

        var scrollState = resetDetailScroll ? null : CaptureDetailScrollState();
        _selectedTopologyNodeId = node.NodeId;
        var detail = node.AttachmentId is null
            ? null
            : App.DeviceTopologies.UsbDetails.TryGetCached(node.AttachmentId, node.NodeId);
        if (detail is not null)
        {
            var addedToProjection = _projection.SetDeviceDetail(detail, expandWhenAdded: true);
            if (addedToProjection)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsLoaded
                        && string.Equals(_selectedRowId, node.NodeId, StringComparison.OrdinalIgnoreCase))
                    {
                        RebuildRows(node.NodeId, resetDetailScroll: false);
                    }
                }, DispatcherPriority.Background);
            }
        }

        SelectedNodeTitleText.Text = node.DisplayName;
        SelectedNodeMetaText.Text = BuildNodeMeta(node, detail);
        OverviewFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildOverview(node, _snapshot);
        ConnectionFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildConnection(node);
        DescriptorFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildDescriptors(node, detail);
        PnpFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildPnp(node);
        RawReportTextBox.Text = UsbDiagnosticNodeFormatter.BuildRawReport(node, _snapshot, detail, _diagnostics);
        CompleteDetailRender(resetDetailScroll, scrollState);

        if (detail is null && UsbDeviceDetailTarget.FromSnapshot(_snapshot, node.NodeId) is not null)
        {
            DescriptorFieldsList.ItemsSource = UsbDiagnosticNodeFormatter
                .BuildDescriptors(node, null)
                .Append(new UsbDiagnosticFieldView(
                    "读取状态", "正在读取深层 descriptor...", "USB detail worker"))
                .ToArray();
            _ = LoadDeviceDetailAsync(node.NodeId, forceRefresh: false);
        }
    }

    private void RenderDescriptor(
        UsbDeviceDetailSnapshot detail,
        UsbDescriptorSelection selection,
        bool resetDetailScroll)
    {
        var scrollState = resetDetailScroll ? null : CaptureDetailScrollState();
        _selectedTopologyNodeId = selection.OwnerNodeId;
        var description = UsbDiagnosticNodeFormatter.DescribeDescriptor(detail, selection);
        SelectedNodeTitleText.Text = description.Title;
        SelectedNodeMetaText.Text = description.Meta;
        OverviewFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildDescriptorOverview(detail, selection);
        ConnectionFieldsList.ItemsSource = null;
        DescriptorFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildDescriptorOverview(detail, selection);
        PnpFieldsList.ItemsSource = null;
        RawReportTextBox.Text = UsbDiagnosticNodeFormatter.BuildDescriptorRawReport(detail, selection);
        CompleteDetailRender(resetDetailScroll, scrollState);
    }

    private void RenderDiagnostic(DeviceTopologyDiagnostic diagnostic, bool resetDetailScroll)
    {
        if (_snapshot is null)
        {
            return;
        }

        var scrollState = resetDetailScroll ? null : CaptureDetailScrollState();
        _selectedTopologyNodeId = null;
        SelectedNodeTitleText.Text = diagnostic.Code;
        SelectedNodeMetaText.Text = $"{diagnostic.Severity} · {diagnostic.NodeId ?? "未归属节点"}";
        OverviewFieldsList.ItemsSource = UsbDiagnosticNodeFormatter.BuildDiagnosticOverview(diagnostic, _snapshot);
        ConnectionFieldsList.ItemsSource = null;
        DescriptorFieldsList.ItemsSource = null;
        PnpFieldsList.ItemsSource = null;
        RawReportTextBox.Text = UsbDiagnosticNodeFormatter.BuildDiagnosticReport(diagnostic, _snapshot);
        CompleteDetailRender(resetDetailScroll, scrollState);
    }

    private async Task LoadDeviceDetailAsync(string nodeId, bool forceRefresh)
    {
        if (_snapshot is null)
        {
            return;
        }

        CancelDetailLoad();
        var loadVersion = ++_detailLoadVersion;
        _detailLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation?.Token ?? CancellationToken.None);
        try
        {
            StatusText.Text = forceRefresh
                ? "正在重新读取当前设备的 USB descriptor..."
                : "正在读取当前设备的 USB descriptor...";
            var detail = forceRefresh
                ? await App.DeviceTopologies.UsbDetails
                    .RefreshAsync(_snapshot, nodeId, _detailLoadCancellation.Token)
                    .ConfigureAwait(true)
                : await App.DeviceTopologies.UsbDetails
                    .EnsureLoadedAsync(_snapshot, nodeId, _detailLoadCancellation.Token)
                    .ConfigureAwait(true);
            if (!IsLoaded || loadVersion != _detailLoadVersion || _snapshot is null)
            {
                return;
            }

            var current = _snapshot.Nodes.FirstOrDefault(node =>
                string.Equals(node.NodeId, detail.DeviceNodeId, StringComparison.OrdinalIgnoreCase));
            if (current is null
                || !string.Equals(current.AttachmentId, detail.AttachmentId, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "设备连接已变化，已忽略过期的 descriptor 结果。";
                return;
            }

            _projection.SetDeviceDetail(detail, expandWhenAdded: !forceRefresh);
            UpdateSummary(_snapshot);
            RebuildRows(_selectedRowId, resetDetailScroll: false);
            StatusText.Text = detail.Diagnostics.HasErrors
                ? "Descriptor 已读取，但解析或设备响应包含诊断错误。"
                : $"Descriptor 已加载：{detail.GeneratedAt:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsLoaded && loadVersion == _detailLoadVersion)
            {
                RenderDetailFailure(nodeId, ex);
            }
        }
    }

    private void RenderDetailFailure(string nodeId, Exception error)
    {
        if (_snapshot?.Nodes.FirstOrDefault(node =>
                string.Equals(node.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)) is not { } node)
        {
            StatusText.Text = $"读取 USB descriptor 失败：{error.Message}";
            return;
        }

        var stale = node.AttachmentId is null
            ? null
            : App.DeviceTopologies.UsbDetails.TryGetCached(node.AttachmentId, node.NodeId);
        if (stale is not null)
        {
            _projection.SetDeviceDetail(stale);
            if (_selectedRowId is not null
                && _projection.TryGetDescriptorSelection(_selectedRowId, out var selection)
                && selection is not null
                && string.Equals(selection.OwnerNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                RenderDescriptor(stale, selection, resetDetailScroll: false);
            }
            else if (string.Equals(_selectedTopologyNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                RenderTopologyNode(node, resetDetailScroll: false);
            }

            SelectedNodeMetaText.Text += $" · descriptor 刷新失败，显示 {stale.GeneratedAt:HH:mm:ss} 缓存";
            RawReportTextBox.Text += $"{Environment.NewLine}{Environment.NewLine}Descriptor Refresh Attempt{Environment.NewLine}  Error: {error.Message}";
            AppendDescriptorStatusField(
                "刷新失败",
                $"{error.Message}；当前显示 {stale.GeneratedAt:HH:mm:ss} 的上次成功结果。");
            StatusText.Text = $"Descriptor 刷新失败，保留 {stale.GeneratedAt:HH:mm:ss} 的上次成功结果。";
            return;
        }

        if (string.Equals(_selectedTopologyNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
        {
            DescriptorFieldsList.ItemsSource = UsbDiagnosticNodeFormatter
                .BuildDescriptors(node, null)
                .Append(new UsbDiagnosticFieldView(
                    "读取失败", error.Message, "USB detail worker"))
                .ToArray();
        }

        StatusText.Text = $"读取 USB descriptor 失败：{error.Message}";
    }

    private void AppendDescriptorStatusField(string label, string value)
    {
        var current = DescriptorFieldsList.ItemsSource is IEnumerable<UsbDiagnosticFieldView> fields
            ? fields
            : [];
        DescriptorFieldsList.ItemsSource = current
            .Append(new UsbDiagnosticFieldView(label, value, "USB detail cache"))
            .ToArray();
    }

    private void CancelDetailLoad()
    {
        _detailLoadCancellation?.Cancel();
        _detailLoadCancellation?.Dispose();
        _detailLoadCancellation = null;
        _detailLoadVersion++;
    }

    private string? ResolveSelectedDescriptorOwner()
    {
        if (_selectedRowId is not null
            && _projection.TryGetDescriptorSelection(_selectedRowId, out var selection)
            && selection is not null)
        {
            return selection.OwnerNodeId;
        }

        return _selectedTopologyNodeId;
    }

    private void CompleteDetailRender(bool resetDetailScroll, DetailScrollState? scrollState)
    {
        var renderVersion = ++_detailRenderVersion;
        if (resetDetailScroll || scrollState is null)
        {
            OverviewScrollViewer.ScrollToTop();
            ConnectionScrollViewer.ScrollToTop();
            DescriptorScrollViewer.ScrollToTop();
            PnpScrollViewer.ScrollToTop();
            RawReportTextBox.ScrollToHome();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (renderVersion == _detailRenderVersion)
            {
                RestoreDetailScrollState(scrollState);
            }
        }, DispatcherPriority.Loaded);
    }

    private DetailScrollState CaptureDetailScrollState()
    {
        var rawScrollViewer = FindVisualChild<ScrollViewer>(RawReportTextBox);
        return new DetailScrollState(
            OverviewScrollViewer.VerticalOffset,
            ConnectionScrollViewer.VerticalOffset,
            DescriptorScrollViewer.VerticalOffset,
            PnpScrollViewer.VerticalOffset,
            rawScrollViewer?.HorizontalOffset ?? 0,
            rawScrollViewer?.VerticalOffset ?? 0);
    }

    private void RestoreDetailScrollState(DetailScrollState state)
    {
        OverviewScrollViewer.ScrollToVerticalOffset(state.OverviewOffset);
        ConnectionScrollViewer.ScrollToVerticalOffset(state.ConnectionOffset);
        DescriptorScrollViewer.ScrollToVerticalOffset(state.DescriptorOffset);
        PnpScrollViewer.ScrollToVerticalOffset(state.PnpOffset);
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

    private void ClearDetails()
    {
        CancelDetailLoad();
        _detailRenderVersion++;
        _selectedRowId = null;
        _selectedTopologyNodeId = null;
        SelectedNodeTitleText.Text = "没有匹配的 USB 条目";
        SelectedNodeMetaText.Text = string.Empty;
        OverviewFieldsList.ItemsSource = null;
        ConnectionFieldsList.ItemsSource = null;
        DescriptorFieldsList.ItemsSource = null;
        PnpFieldsList.ItemsSource = null;
        RawReportTextBox.Clear();
    }

    private void CopyNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RawReportTextBox.Text))
        {
            Clipboard.SetText(RawReportTextBox.Text);
            StatusText.Text = "当前 USB 诊断已复制。";
        }
    }

    private void CopyFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UsbDiagnosticFieldView field }
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
            Title = "保存 USB 节点诊断",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-USB-Node-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
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

    private UsbDiagnosticTreeFilter ReadFilter()
    {
        var tag = (FilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<UsbDiagnosticTreeFilter>(tag, out var filter)
            ? filter
            : UsbDiagnosticTreeFilter.All;
    }

    private static string BuildNodeMeta(
        UsbTopologyNode node,
        UsbDeviceDetailSnapshot? detail)
    {
        var parts = new List<string> { node.Kind.ToString() };
        if (node.Port is not null)
        {
            parts.Add($"Port {node.Port.PortChain}");
            parts.Add(node.Port.ConnectionStatus.ToString());
            if (node.Port.ConnectionSpeed != UsbConnectionSpeed.Unknown)
            {
                parts.Add(node.Port.ConnectionSpeed.ToString());
            }
        }

        if (node.DeviceDescriptor is not null)
        {
            parts.Add(node.DeviceDescriptor.VendorProduct);
            parts.Add($"USB {node.DeviceDescriptor.UsbVersion}");
        }

        if (detail is not null)
        {
            parts.Add($"{detail.Configurations.Length} configurations loaded");
        }

        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<DeviceTopologyDiagnostic> MergeDiagnostics(
        IReadOnlyList<DeviceTopologyDiagnostic> snapshotDiagnostics,
        IReadOnlyList<DeviceTopologyDiagnostic> attemptDiagnostics) =>
        snapshotDiagnostics.Concat(attemptDiagnostics).Distinct().ToArray();

    private sealed record DetailScrollState(
        double OverviewOffset,
        double ConnectionOffset,
        double DescriptorOffset,
        double PnpOffset,
        double RawHorizontalOffset,
        double RawVerticalOffset);
}

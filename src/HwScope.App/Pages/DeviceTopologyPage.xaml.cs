using System.IO;
using System.Windows;
using System.Windows.Controls;
using HwScope.App.Pages.DeviceTopology;
using HwScope.App.Topology.Controls;
using HwScope.App.Topology.Model;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using HwScope.Core.Hardware.DeviceTopology.Usb;
using Microsoft.Win32;

namespace HwScope.App.Pages;

public partial class DeviceTopologyPage : UserControl
{
    private readonly HashSet<string> _expandedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manuallyCollapsedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usbExpandedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usbManuallyCollapsedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private PciTopologySnapshot? _snapshot;
    private UsbTopologySnapshot? _usbSnapshot;
    private string? _selectedNodeId;
    private string? _usbSelectedNodeId;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _usbDetailLoadCancellation;
    private int _usbDetailSelectionVersion;
    private bool _usbLoadStarted;

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
        App.DeviceTopologies.UsbStateChanged += DeviceTopologies_UsbStateChanged;
        try
        {
            SetStatus("正在枚举 PCI Express 设备...");
            var result = await App.DeviceTopologies.EnsurePciLoadedAsync(_loadCancellation.Token).ConfigureAwait(true);
            RenderRefreshResult(result);
            if (TopologyTabs.SelectedItem == PciTab)
            {
                SetStatus(FormatRefreshStatus(result, "PCI Express 拓扑已加载。"));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DeviceTopologyPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.DeviceTopologies.PciStateChanged -= DeviceTopologies_PciStateChanged;
        App.DeviceTopologies.UsbStateChanged -= DeviceTopologies_UsbStateChanged;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _usbDetailLoadCancellation?.Cancel();
        _usbDetailLoadCancellation?.Dispose();
        _usbDetailLoadCancellation = null;
        _usbDetailSelectionVersion++;
    }

    private void DeviceTopologies_PciStateChanged(object? sender, PciTopologyRefreshResult result)
    {
        if (IsLoaded)
        {
            RenderRefreshResult(result);
        }
    }

    private void DeviceTopologies_UsbStateChanged(object? sender, UsbTopologyRefreshResult result)
    {
        if (IsLoaded)
        {
            RenderUsbRefreshResult(result);
        }
    }

    private async void TopologyTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TopologyTabs.SelectedItem != UsbTab)
        {
            if (IsLoaded && _snapshot is not null)
            {
                UpdatePciHeader(_snapshot);
            }
            return;
        }

        if (_usbSnapshot is not null)
        {
            UpdateUsbHeader(_usbSnapshot);
        }

        if (_usbLoadStarted)
        {
            return;
        }

        _usbLoadStarted = true;
        try
        {
            SetStatus("正在枚举 USB Host Controller 与物理端口...");
            UsbMapHintText.Text = "正在读取 USB 物理端口...";
            var result = await App.DeviceTopologies
                .EnsureUsbLoadedAsync(_loadCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            RenderUsbRefreshResult(result);
            if (TopologyTabs.SelectedItem == UsbTab)
            {
                SetStatus(FormatUsbRefreshStatus(result, "USB 物理端口拓扑已加载。"));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_usbSnapshot is null)
            {
                _usbLoadStarted = false;
            }
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TopologyTabs.SelectedItem == UsbTab)
            {
                SetStatus("正在刷新 USB 物理端口拓扑...");
                var usbResult = await App.DeviceTopologies
                    .RefreshUsbAsync(_loadCancellation?.Token ?? CancellationToken.None)
                    .ConfigureAwait(true);
                RenderUsbRefreshResult(usbResult);
                RenderUsbSelection(forceRefresh: true);
                if (TopologyTabs.SelectedItem == UsbTab)
                {
                    SetStatus(FormatUsbRefreshStatus(usbResult, "USB 物理端口拓扑已刷新。"));
                }
                return;
            }

            SetStatus("正在刷新 PCI Express 拓扑...");
            var result = await App.DeviceTopologies.RefreshPciAsync(_loadCancellation?.Token ?? CancellationToken.None).ConfigureAwait(true);
            RenderRefreshResult(result);
            if (TopologyTabs.SelectedItem == PciTab)
            {
                SetStatus(FormatRefreshStatus(result, "PCI Express 拓扑已刷新。"));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TopologyTabs.SelectedItem == UsbTab)
        {
            if (_usbSnapshot is null)
            {
                return;
            }

            Clipboard.SetText(UsbTopologyReportFormatter.Format(_usbSnapshot));
            SetStatus("USB 物理端口拓扑报告已复制到剪贴板。");
            return;
        }

        if (_snapshot is null)
        {
            return;
        }

        Clipboard.SetText(PciTopologyReportFormatter.Format(_snapshot));
        SetStatus("PCI Express 拓扑报告已复制到剪贴板。");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TopologyTabs.SelectedItem == UsbTab)
        {
            SaveUsbReport();
            return;
        }

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

    private void SaveUsbReport()
    {
        if (_usbSnapshot is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 USB 物理端口拓扑报告",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-USB-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, UsbTopologyReportFormatter.Format(_usbSnapshot));
        SetStatus($"USB 物理端口拓扑报告已保存：{dialog.FileName}");
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

    private void UsbTopologyMap_ItemSelected(object? sender, TopologyItemSelectedEventArgs e)
    {
        _usbSelectedNodeId = e.ItemId;
        UsbTopologyMap.SelectedItemId = e.ItemId;
        RenderUsbSelection();
    }

    private void UsbTopologyMap_ExpansionToggled(object? sender, TopologyExpansionToggledEventArgs e)
    {
        if (e.IsExpanded)
        {
            _usbExpandedNodeIds.Add(e.ItemId);
            _usbManuallyCollapsedNodeIds.Remove(e.ItemId);
        }
        else
        {
            _usbExpandedNodeIds.Remove(e.ItemId);
            _usbManuallyCollapsedNodeIds.Add(e.ItemId);
        }

        RenderUsbMap();
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

        if (TopologyTabs.SelectedItem == PciTab)
        {
            UpdatePciHeader(snapshot);
        }
        RenderMap();
        RenderSelection();
    }

    private void UpdatePciHeader(PciTopologySnapshot snapshot)
    {
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

    private void RenderUsbSnapshot(UsbTopologySnapshot snapshot)
    {
        var previousSelection = _usbSnapshot?.Nodes.FirstOrDefault(node => node.NodeId == _usbSelectedNodeId);
        _usbSnapshot = snapshot;
        var currentIds = snapshot.Nodes.Select(node => node.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _usbExpandedNodeIds.RemoveWhere(nodeId => !currentIds.Contains(nodeId));
        _usbManuallyCollapsedNodeIds.RemoveWhere(nodeId => !currentIds.Contains(nodeId));
        _usbExpandedNodeIds.UnionWith(UsbCompactTopologyAdapter
            .CreateDefaultExpansion(snapshot)
            .Where(nodeId => !_usbManuallyCollapsedNodeIds.Contains(nodeId)));

        if (_usbSelectedNodeId is null || !currentIds.Contains(_usbSelectedNodeId))
        {
            _usbSelectedNodeId = previousSelection?.ParentNodeId is { } previousParentId
                    && currentIds.Contains(previousParentId)
                ? previousParentId
                : snapshot.Nodes.FirstOrDefault(node => node.Kind == UsbTopologyNodeKind.Device)?.NodeId
                ?? snapshot.Nodes.FirstOrDefault(node => node.Kind == UsbTopologyNodeKind.Hub)?.NodeId
                ?? snapshot.Nodes.FirstOrDefault()?.NodeId;
        }

        if (TopologyTabs.SelectedItem == UsbTab)
        {
            UpdateUsbHeader(snapshot);
        }
        RenderUsbMap();
        RenderUsbSelection();
    }

    private void UpdateUsbHeader(UsbTopologySnapshot snapshot)
    {
        var controllerCount = snapshot.HostControllerNodeIds.Count;
        var hubCount = snapshot.Nodes.Count(node => node.Kind is UsbTopologyNodeKind.RootHub or UsbTopologyNodeKind.Hub);
        var deviceCount = snapshot.Nodes.Count(node => node.Kind == UsbTopologyNodeKind.Device);
        var portCount = snapshot.Nodes.Count(node => node.Kind == UsbTopologyNodeKind.Port);
        var connectedCount = snapshot.Nodes.Count(node =>
            node.Kind == UsbTopologyNodeKind.Port
            && node.Port?.ConnectionStatus is not null
                and not UsbConnectionStatus.NoDeviceConnected
                and not UsbConnectionStatus.Unknown);
        var problemAttachments = snapshot.Nodes
            .Where(node => node.Kind == UsbTopologyNodeKind.Port
                && node.Port?.ConnectionStatus is not null
                    and not UsbConnectionStatus.NoDeviceConnected
                    and not UsbConnectionStatus.DeviceConnected
                    and not UsbConnectionStatus.Unknown)
            .Select(node => node.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        problemAttachments.UnionWith(snapshot.Nodes
            .Where(node => node.Identity?.Status.HasProblem == true)
            .Select(node => node.AttachmentId ?? node.NodeId));
        var problemCount = problemAttachments.Count;
        SubtitleText.Text = $"{controllerCount} 个控制器 · {hubCount} 个 Hub · {portCount} 个端口 · {connectedCount} 个已连接 · {deviceCount} 个设备 · {problemCount} 个异常 · {snapshot.GeneratedAt:HH:mm:ss}";
        UsbMapHintText.Text = snapshot.Diagnostics.Entries.Count == 0
            ? "已连接分支默认展开"
            : $"{snapshot.Diagnostics.Entries.Count} 条诊断";
    }

    private void RenderUsbRefreshResult(UsbTopologyRefreshResult result)
    {
        RenderUsbSnapshot(result.Snapshot);
        if (result.IsStale)
        {
            UsbMapHintText.Text = $"刷新失败，显示 {result.Snapshot.GeneratedAt:HH:mm:ss} 的结果";
        }
        else if (result.CollectionFailed)
        {
            UsbMapHintText.Text = result.AttemptDiagnostics.Entries.FirstOrDefault()?.Message ?? "USB 枚举失败";
        }
    }

    private static string FormatUsbRefreshStatus(UsbTopologyRefreshResult result, string success)
    {
        if (result.IsStale)
        {
            return $"USB 刷新失败，保留 {result.Snapshot.GeneratedAt:HH:mm:ss} 的上次成功结果。";
        }

        if (result.CollectionFailed)
        {
            return result.AttemptDiagnostics.Entries.FirstOrDefault()?.Message ?? "USB 枚举失败。";
        }

        return result.Snapshot.Diagnostics.HasErrors ? "USB 枚举完成，但部分控制器或 Hub 存在诊断错误。" : success;
    }

    private void RenderUsbMap()
    {
        if (_usbSnapshot is null)
        {
            UsbTopologyMap.Document = TopologyDocument.Empty;
            return;
        }

        UsbTopologyMap.Document = UsbCompactTopologyAdapter.ToDocument(_usbSnapshot, _usbExpandedNodeIds);
        UsbTopologyMap.SelectedItemId = _usbSelectedNodeId;
    }

    private void RenderUsbSelection(bool forceRefresh = false)
    {
        _usbDetailLoadCancellation?.Cancel();
        _usbDetailLoadCancellation?.Dispose();
        _usbDetailLoadCancellation = null;
        var selectionVersion = ++_usbDetailSelectionVersion;
        var node = _usbSnapshot?.Nodes.FirstOrDefault(candidate => candidate.NodeId == _usbSelectedNodeId);
        if (node is null)
        {
            UsbSelectedTitleText.Text = "请选择一个 USB 节点";
            UsbSelectedMetaText.Text = string.Empty;
            UsbSelectedPathText.Text = string.Empty;
            UsbDetailSectionsList.ItemsSource = null;
            return;
        }

        UsbSelectedTitleText.Text = node.DisplayName;
        UsbSelectedMetaText.Text = BuildUsbMeta(node);
        UsbSelectedPathText.Text = BuildUsbBreadcrumb(node);
        var sections = BuildUsbSections(node).ToList();
        var target = _usbSnapshot is null
            ? null
            : UsbDeviceDetailTarget.FromSnapshot(_usbSnapshot, node.NodeId);
        if (target is null)
        {
            UsbDetailSectionsList.ItemsSource = sections;
            return;
        }

        var cached = App.DeviceTopologies.UsbDetails.TryGetCached(target.AttachmentId, target.DeviceNodeId);
        if (!forceRefresh && cached is not null)
        {
            sections.AddRange(BuildUsbDetailSections(cached));
            UsbDetailSectionsList.ItemsSource = sections;
            return;
        }

        sections.Add(new TopologyDetailSectionView(
            "深层描述符",
            [new TopologyDetailFieldView("状态", "正在读取 configuration、interface、endpoint 与 BOS...")]));
        UsbDetailSectionsList.ItemsSource = sections;
        _usbDetailLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation?.Token ?? CancellationToken.None);
        _ = LoadUsbDetailAsync(
            _usbSnapshot!,
            node.NodeId,
            selectionVersion,
            forceRefresh,
            _usbDetailLoadCancellation.Token);
    }

    private async Task LoadUsbDetailAsync(
        UsbTopologySnapshot snapshot,
        string nodeId,
        int selectionVersion,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            var detailTask = forceRefresh
                ? App.DeviceTopologies.UsbDetails.RefreshAsync(snapshot, nodeId, cancellationToken)
                : App.DeviceTopologies.UsbDetails.EnsureLoadedAsync(snapshot, nodeId, cancellationToken);
            var detail = await detailTask.ConfigureAwait(true);
            if (!IsLoaded
                || selectionVersion != _usbDetailSelectionVersion
                || !string.Equals(_usbSelectedNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var node = _usbSnapshot?.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                return;
            }

            var sections = BuildUsbSections(node).ToList();
            sections.AddRange(BuildUsbDetailSections(detail));
            UsbDetailSectionsList.ItemsSource = sections;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsLoaded
                && selectionVersion == _usbDetailSelectionVersion
                && string.Equals(_usbSelectedNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                var node = _usbSnapshot?.Nodes.FirstOrDefault(candidate =>
                    string.Equals(candidate.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
                var sections = node is null ? [] : BuildUsbSections(node).ToList();
                sections.Add(new TopologyDetailSectionView(
                    "深层描述符",
                    [new TopologyDetailFieldView("读取失败", ex.Message)]));
                UsbDetailSectionsList.ItemsSource = sections;
            }
        }
    }

    private string BuildUsbBreadcrumb(UsbTopologyNode node)
    {
        if (_usbSnapshot is null)
        {
            return string.Empty;
        }

        var byId = _usbSnapshot.Nodes.ToDictionary(item => item.NodeId, StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();
        UsbTopologyNode? current = node;
        while (current is not null)
        {
            labels.Add(current.Kind == UsbTopologyNodeKind.Port
                ? $"Port {current.Port?.PortChain}"
                : current.DisplayName);
            current = current.ParentNodeId is not null && byId.TryGetValue(current.ParentNodeId, out var parent)
                ? parent
                : null;
        }

        labels.Reverse();
        return string.Join(" > ", labels);
    }

    private static string BuildUsbMeta(UsbTopologyNode node)
    {
        var parts = new List<string> { FormatUsbKind(node.Kind) };
        if (node.Port is not null)
        {
            parts.Add($"Port {node.Port.PortChain}");
            parts.Add(node.Port.ConnectionStatus.ToString());
            if (node.Port.ConnectionSpeed != UsbConnectionSpeed.Unknown)
            {
                parts.Add(FormatUsbSpeed(node.Port.ConnectionSpeed));
            }
        }

        if (node.DeviceDescriptor is not null)
        {
            parts.Add(node.DeviceDescriptor.VendorProduct);
        }

        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<TopologyDetailSectionView> BuildUsbSections(UsbTopologyNode node)
    {
        var identity = node.Identity;
        var port = node.Port;
        var descriptor = node.DeviceDescriptor;
        return
        [
            new("身份与位置",
            [
                new("节点类型", FormatUsbKind(node.Kind)),
                new("名称", node.DisplayName),
                new("端口链", port?.PortChain ?? "不适用"),
                new("Location Path", identity?.LocationPaths.FirstOrDefault() ?? "未报告"),
                new("Instance ID", identity?.InstanceId ?? "未报告"),
                new("Container ID", identity?.ContainerId?.ToString("D") ?? "未报告")
            ]),
            new("连接与端口",
            [
                new("连接状态", port?.ConnectionStatus.ToString() ?? "不适用"),
                new("当前速率", port is null ? "不适用" : FormatUsbSpeed(port.ConnectionSpeed)),
                new("端口协议", port is null ? "不适用" : FormatUsbProtocols(port.SupportedProtocols)),
                new("用户可连接", FormatNullable(port?.IsUserConnectable)),
                new("Type-C", FormatNullable(port?.IsTypeC)),
                new("Companion Port", port?.CompanionPortNumber?.ToString() ?? "未报告"),
                new("设备地址", port?.DeviceAddress.ToString() ?? "未报告")
            ]),
            new("设备描述符",
            [
                new("VID / PID", descriptor?.VendorProduct ?? "未报告"),
                new("bcdUSB", descriptor is null ? "未报告" : descriptor.UsbVersion),
                new("bcdDevice", descriptor is null ? "未报告" : $"0x{descriptor.DeviceVersionBcd:X4}"),
                new("Class / Subclass", descriptor is null ? "未报告" : $"{descriptor.DeviceClass:X2} / {descriptor.DeviceSubClass:X2}"),
                new("Protocol", descriptor is null ? "未报告" : $"0x{descriptor.DeviceProtocol:X2}"),
                new("Configurations", descriptor?.ConfigurationCount.ToString() ?? "未报告")
            ]),
            new("PnP 与原始信息",
            [
                new("厂商", identity?.Manufacturer ?? "未报告"),
                new("Service", identity?.Service ?? "未报告"),
                new("Problem Code", identity?.Status.ProblemCode?.ToString() ?? "无"),
                new("Hardware ID", identity?.HardwareIds.FirstOrDefault() ?? "未报告"),
                new("Driver Key", Empty(node.DriverKey)),
                new("Hub Symbolic Name", node.Hub is null ? "未报告" : Empty(node.Hub.SymbolicName))
            ])
        ];
    }

    private static IReadOnlyList<TopologyDetailSectionView> BuildUsbDetailSections(UsbDeviceDetailSnapshot detail)
    {
        const int maximumDescriptorSections = 96;
        const int maximumEndpointRowsPerInterface = 32;
        var sections = new List<TopologyDetailSectionView>
        {
            new("描述符字符串",
            [
                new("Manufacturer", detail.Manufacturer ?? "未报告"),
                new("Product", detail.Product ?? "未报告"),
                new("Serial Number", detail.SerialNumber ?? "未报告"),
                new("Languages", detail.Languages.IsDefaultOrEmpty
                    ? "未报告"
                    : string.Join(" / ", detail.Languages.Select(language => language.DisplayName))),
                new("缓存时间", detail.GeneratedAt.ToString("HH:mm:ss"))
            ])
        };
        var descriptorSectionCount = 0;
        var sectionLimitReached = false;
        var contentTruncated = false;

        bool TryAddDescriptorSection(TopologyDetailSectionView section)
        {
            if (descriptorSectionCount >= maximumDescriptorSections)
            {
                sectionLimitReached = true;
                contentTruncated = true;
                return false;
            }

            sections.Add(section);
            descriptorSectionCount++;
            return true;
        }

        foreach (var configuration in detail.Configurations)
        {
            if (!TryAddDescriptorSection(new TopologyDetailSectionView(
                $"Configuration {configuration.DescriptorIndex}",
                [
                    new("Configuration Value", configuration.ConfigurationValue.ToString()),
                    new("Description", configuration.Description ?? "未报告"),
                    new("Total Length", $"{configuration.TotalLength} bytes"),
                    new("Interfaces", $"{configuration.Interfaces.Length} parsed / {configuration.DeclaredInterfaceCount} declared"),
                    new("Power", configuration.IsSelfPowered
                        ? $"Self-powered, {configuration.MaximumPowerMilliamps} mA requested"
                        : $"Bus-powered, {configuration.MaximumPowerMilliamps} mA"),
                    new("Remote Wakeup", configuration.SupportsRemoteWakeup ? "支持" : "不支持"),
                    new("Additional Descriptors", configuration.AdditionalDescriptors.Length.ToString())
                ])))
            {
                break;
            }

            foreach (var item in configuration.InterfaceAssociations)
            {
                if (!TryAddDescriptorSection(new TopologyDetailSectionView(
                    $"IAD · Interface {item.FirstInterface}-{item.FirstInterface + item.InterfaceCount - 1}",
                    [
                        new("Description", item.Description ?? "未报告"),
                        new("Class / Subclass / Protocol", $"0x{item.FunctionClass:X2} / 0x{item.FunctionSubClass:X2} / 0x{item.FunctionProtocol:X2}")
                    ])))
                {
                    break;
                }
            }

            if (sectionLimitReached)
            {
                break;
            }

            foreach (var item in configuration.Interfaces)
            {
                var rows = new List<TopologyDetailFieldView>
                {
                    new("Alternate Setting", item.AlternateSetting.ToString()),
                    new("Description", item.Description ?? "未报告"),
                    new("Class / Subclass / Protocol", $"0x{item.InterfaceClass:X2} / 0x{item.InterfaceSubClass:X2} / 0x{item.InterfaceProtocol:X2}"),
                    new("Endpoints", $"{item.Endpoints.Length} parsed / {item.DeclaredEndpointCount} declared")
                };
                foreach (var endpoint in item.Endpoints.Take(maximumEndpointRowsPerInterface))
                {
                    var companion = endpoint.SuperSpeedCompanion is null
                        ? string.Empty
                        : $", burst {endpoint.SuperSpeedCompanion.MaximumBurst}, {endpoint.SuperSpeedCompanion.BytesPerInterval} bytes/interval";
                    rows.Add(new TopologyDetailFieldView(
                        $"Endpoint 0x{endpoint.Address:X2}",
                        $"{endpoint.Direction} · {endpoint.TransferType} · {endpoint.MaximumPacketBytes} bytes · interval {endpoint.Interval}{companion}"));
                }

                if (item.Endpoints.Length > maximumEndpointRowsPerInterface)
                {
                    contentTruncated = true;
                    rows.Add(new TopologyDetailFieldView(
                        "更多 Endpoint",
                        $"另有 {item.Endpoints.Length - maximumEndpointRowsPerInterface} 项未在主页面展开。"));
                }

                if (!TryAddDescriptorSection(new TopologyDetailSectionView(
                    $"Interface {item.InterfaceNumber} · Alt {item.AlternateSetting}",
                    rows)))
                {
                    break;
                }
            }

            if (sectionLimitReached)
            {
                break;
            }
        }

        if (contentTruncated)
        {
            sections.Add(new TopologyDetailSectionView(
                "显示限制",
                [new TopologyDetailFieldView(
                    "主页面",
                    $"为保持响应速度，最多显示 {maximumDescriptorSections} 个 descriptor section；完整内容保留在详情缓存中。")]
            ));
        }

        if (detail.Bos is not null)
        {
            sections.Add(new TopologyDetailSectionView(
                "BOS Capabilities",
                [
                    new("Total Length", $"{detail.Bos.TotalLength} bytes"),
                    new("Capabilities", detail.Bos.Capabilities.IsDefaultOrEmpty
                        ? "无"
                        : string.Join(" / ", detail.Bos.Capabilities.Select(capability => capability.DisplayName))),
                    new("Declared", detail.Bos.DeclaredCapabilityCount.ToString())
                ]));
        }

        if (detail.Diagnostics.Entries.Count > 0)
        {
            sections.Add(new TopologyDetailSectionView(
                "Descriptor Diagnostics",
                detail.Diagnostics.Entries.Select((entry, index) =>
                    new TopologyDetailFieldView($"{entry.Severity} {index + 1}", entry.Message)).ToArray()));
        }

        return sections;
    }

    private static string FormatUsbKind(UsbTopologyNodeKind kind)
    {
        return kind switch
        {
            UsbTopologyNodeKind.HostController => "Host Controller",
            UsbTopologyNodeKind.RootHub => "Root Hub",
            UsbTopologyNodeKind.Hub => "USB Hub",
            UsbTopologyNodeKind.Port => "Physical Port",
            UsbTopologyNodeKind.Device => "USB Device",
            _ => "Error"
        };
    }

    private static string FormatUsbSpeed(UsbConnectionSpeed speed)
    {
        return speed switch
        {
            UsbConnectionSpeed.Low => "Low-Speed (1.5 Mbps)",
            UsbConnectionSpeed.Full => "Full-Speed (12 Mbps)",
            UsbConnectionSpeed.High => "High-Speed (480 Mbps)",
            UsbConnectionSpeed.Super => "SuperSpeed (5 Gbps)",
            UsbConnectionSpeed.SuperPlus => "SuperSpeedPlus",
            _ => "未协商 / 未知"
        };
    }

    private static string FormatUsbProtocols(UsbSupportedProtocols protocols)
    {
        if (protocols == UsbSupportedProtocols.None)
        {
            return "未报告";
        }

        return string.Join(" / ", new[]
        {
            protocols.HasFlag(UsbSupportedProtocols.Usb11) ? "USB 1.1" : null,
            protocols.HasFlag(UsbSupportedProtocols.Usb20) ? "USB 2.0" : null,
            protocols.HasFlag(UsbSupportedProtocols.Usb30) ? "USB 3.x" : null
        }.Where(value => value is not null));
    }

    private static string FormatNullable(bool? value)
    {
        return value switch
        {
            true => "是",
            false => "否",
            null => "未报告"
        };
    }

    private void SetStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }
}

public sealed record TopologyDetailSectionView(string Title, IReadOnlyList<TopologyDetailFieldView> Rows);

public sealed record TopologyDetailFieldView(string Label, string Value);

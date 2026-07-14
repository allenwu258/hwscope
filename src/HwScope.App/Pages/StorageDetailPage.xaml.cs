using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HwScope.Core.Hardware.Storage;
using Microsoft.Win32;

namespace HwScope.App.Pages;

public partial class StorageDetailPage : UserControl
{
    private string? _selectedDeviceId;
    private StorageDetailReport? _currentReport;
    private int _refreshVersion;
    private CancellationTokenSource? _selectionCancellation;
    private bool _loadedOnce;
    private bool _isSubscribed;
    private StorageAttributeFilter _attributeFilter;

    public StorageDetailPage()
    {
        InitializeComponent();
        SetBusy(false);
        Loaded += StorageDetailPage_Loaded;
        Unloaded += StorageDetailPage_Unloaded;
    }

    public event EventHandler<string>? StatusChanged;

    private async void StorageDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        if (_loadedOnce)
        {
            RenderDeviceTiles();
            return;
        }

        _loadedOnce = true;
        try
        {
            var snapshot = await App.HardwarePreload.EnsureLoadedAsync().ConfigureAwait(true);
            App.StorageDetails.SynchronizeInventory(snapshot);
            RenderDeviceTiles();
            var initial = SelectInitialDevice();
            if (initial is not null)
            {
                await SelectDeviceAsync(initial, forceRefresh: false);
            }
        }
        catch (Exception ex)
        {
            StorageSubtitleText.Text = $"存储设备读取失败：{ex.Message}";
            SetStatus(StorageSubtitleText.Text);
        }
    }

    private void StorageDetailPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        _selectionCancellation?.Cancel();
    }

    private async void DeviceTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StorageDeviceTileView tile })
        {
            await SelectDeviceAsync(tile.Id, forceRefresh: false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is not null)
        {
            await SelectDeviceAsync(_selectedDeviceId, forceRefresh: true);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null)
        {
            return;
        }

        Clipboard.SetText(StorageDetailReportFormatter.Format(_currentReport));
        SetStatus("存储设备详情已复制到剪贴板。");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null)
        {
            return;
        }

        var disk = _currentReport.Identity.PhysicalDriveNumber.IsAvailable
            ? _currentReport.Identity.PhysicalDriveNumber.Value.ToString()
            : "unknown";
        var dialog = new SaveFileDialog
        {
            Title = "保存存储设备详情",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-Storage-Disk{disk}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, StorageDetailReportFormatter.Format(_currentReport));
        SetStatus($"存储设备详情已保存：{dialog.FileName}");
    }

    private void AllAttributesButton_Click(object sender, RoutedEventArgs e)
    {
        _attributeFilter = StorageAttributeFilter.All;
        RenderAttributes();
    }

    private void WarningAttributesButton_Click(object sender, RoutedEventArgs e)
    {
        _attributeFilter = StorageAttributeFilter.Warning;
        RenderAttributes();
    }

    private void CriticalAttributesButton_Click(object sender, RoutedEventArgs e)
    {
        _attributeFilter = StorageAttributeFilter.Critical;
        RenderAttributes();
    }

    private async Task SelectDeviceAsync(string stableId, bool forceRefresh)
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        _selectedDeviceId = stableId;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = new CancellationTokenSource();
        var token = _selectionCancellation.Token;

        RenderDeviceTiles();
        if (App.StorageDetails.TryGetCached(stableId) is { } cached)
        {
            Render(cached);
        }
        else
        {
            RenderLoadingState(stableId);
        }

        SetBusy(true);
        SetStatus("正在读取选中存储设备的健康信息...");
        try
        {
            var report = forceRefresh
                ? await App.StorageDetails.RefreshAsync(stableId, token).ConfigureAwait(true)
                : await App.StorageDetails.EnsureLoadedAsync(stableId, token).ConfigureAwait(true);
            if (version != _refreshVersion || stableId != _selectedDeviceId)
            {
                return;
            }

            Render(report);
            SetStatus("存储设备详情已刷新。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (version == _refreshVersion)
            {
                HealthReasonText.Text = $"读取失败：{ex.Message}";
                SetStatus($"存储设备详情读取失败：{ex.Message}");
            }
        }
        finally
        {
            if (version == _refreshVersion)
            {
                SetBusy(false);
            }
        }
    }

    private void Render(StorageDetailReport report)
    {
        _currentReport = report;
        _selectedDeviceId = report.Identity.StableId;
        SelectedDeviceTitleText.Text = report.Identity.Model.DisplayText;
        SelectedDeviceMetaText.Text = BuildDeviceMeta(report);
        StorageSubtitleText.Text = $"{App.StorageDetails.Devices.Count} 个物理设备 · 检测时间 {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        HealthStatusText.Text = report.Health.StatusText;
        HealthReasonText.Text = report.Health.StatusReason;
        TemperatureText.Text = report.Health.TemperatureCelsius.DisplayText;
        RemainingLifeText.Text = report.Health.RemainingLifePercent.IsAvailable
            ? $"预计剩余寿命 {report.Health.RemainingLifePercent.DisplayText}"
            : report.Health.RemainingLifePercent.DisplayText;
        ApplyHealthBrush(report.Health.Status);
        HeaderChipsList.ItemsSource = BuildHeaderChips(report);
        SectionList.ItemsSource = BuildSections(report);
        RenderAttributes();
        RenderPartitions(report);
        NotesList.ItemsSource = BuildNotes(report);
        NotesPanel.Visibility = NotesList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderDeviceTiles();
    }

    private void RenderLoadingState(string stableId)
    {
        var device = App.StorageDetails.Devices.FirstOrDefault(candidate => candidate.StableId == stableId);
        _currentReport = null;
        SelectedDeviceTitleText.Text = device?.Model ?? "正在读取...";
        SelectedDeviceMetaText.Text = device is null
            ? string.Empty
            : $"Disk {device.PhysicalDriveNumber?.ToString() ?? "?"} · {StorageField.FormatDecimalBytes(device.CapacityBytes)} · {device.InterfaceType}";
        HealthStatusText.Text = "读取中";
        HealthReasonText.Text = "正在查询 Windows Storage API 和协议健康数据。";
        TemperatureText.Text = StorageField.UnknownText;
        RemainingLifeText.Text = StorageField.PendingHealthText;
        ApplyHealthBrush(StorageHealthStatus.Unknown);
        HeaderChipsList.ItemsSource = Array.Empty<string>();
        SectionList.ItemsSource = Array.Empty<StorageSectionView>();
        AttributeGrid.ItemsSource = null;
        NoAttributesText.Visibility = Visibility.Visible;
        PartitionList.ItemsSource = null;
        NoPartitionsText.Visibility = Visibility.Visible;
        NotesList.ItemsSource = null;
    }

    private void RenderDeviceTiles()
    {
        var devices = App.StorageDetails.Devices;
        DeviceTilesList.ItemsSource = devices.Select(device => ToTile(device, device.StableId == _selectedDeviceId)).ToList();
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedDevicePanel.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = devices.Count > 0 && _selectedDeviceId is not null;
        if (devices.Count == 0)
        {
            StorageSubtitleText.Text = "未识别到物理存储设备。";
        }
    }

    private StorageDeviceTileView ToTile(StorageDeviceDescriptor device, bool selected)
    {
        var cached = App.StorageDetails.TryGetCached(device.StableId);
        var status = cached?.Health.Status ?? StorageHealthStatus.Unknown;
        var statusText = cached?.Health.StatusText ?? "未读取";
        var temperature = cached?.Health.TemperatureCelsius.IsAvailable == true
            ? cached.Health.TemperatureCelsius.DisplayText
            : string.Empty;
        var volumes = cached is null
            ? string.Empty
            : string.Join(' ', cached.Volumes.Select(volume => volume.DriveLetter).Where(value => !string.IsNullOrWhiteSpace(value)));
        var bus = cached?.Interface.BusType.DisplayText ?? device.InterfaceType;
        var title = $"Disk {device.PhysicalDriveNumber?.ToString() ?? "?"} · {bus}";
        return new StorageDeviceTileView(device.StableId, title, device.Model, status, statusText, temperature, volumes, selected);
    }

    private void RenderAttributes()
    {
        if (_currentReport is null)
        {
            return;
        }

        var rows = _currentReport.Attributes.AsEnumerable();
        rows = _attributeFilter switch
        {
            StorageAttributeFilter.Warning => rows.Where(row => row.Severity is StorageAttributeSeverity.Caution or StorageAttributeSeverity.Critical),
            StorageAttributeFilter.Critical => rows.Where(row => row.Severity == StorageAttributeSeverity.Critical),
            _ => rows
        };
        var list = rows.ToList();
        AttributeGrid.ItemsSource = list;
        var showAtaColumns = _currentReport.Interface.Protocol == StorageProtocolKind.Ata;
        CurrentAttributeColumn.Visibility = showAtaColumns ? Visibility.Visible : Visibility.Collapsed;
        WorstAttributeColumn.Visibility = showAtaColumns ? Visibility.Visible : Visibility.Collapsed;
        ThresholdAttributeColumn.Visibility = showAtaColumns ? Visibility.Visible : Visibility.Collapsed;
        NoAttributesText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AttributeGrid.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AllAttributesButton.Background = _attributeFilter == StorageAttributeFilter.All ? (Brush)FindResource("HwScopeActiveViewBrush") : Brushes.Transparent;
        WarningAttributesButton.Background = _attributeFilter == StorageAttributeFilter.Warning ? (Brush)FindResource("HwScopeActiveViewBrush") : Brushes.Transparent;
        CriticalAttributesButton.Background = _attributeFilter == StorageAttributeFilter.Critical ? (Brush)FindResource("HwScopeActiveViewBrush") : Brushes.Transparent;
    }

    private void RenderPartitions(StorageDetailReport report)
    {
        var rows = report.Partitions.Select(partition =>
        {
            var volume = FindVolume(partition, report.Volumes);
            var name = volume is not null && !string.IsNullOrWhiteSpace(volume.DriveLetter)
                ? volume.DriveLetter
                : $"分区 {partition.PartitionNumber}";
            var detail = string.Join(" · ", new[]
            {
                partition.Style,
                partition.Type,
                volume?.Label,
                volume?.FileSystem,
                volume?.HealthStatus,
                volume is null || volume.Roles.Count == 0 ? null : string.Join('/', volume.Roles),
                FormatAccessPaths(partition.AccessPaths)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var offset = $"偏移 {StorageField.FormatBinaryBytes(partition.OffsetBytes)}";
            var free = volume is null ? offset : $"可用 {StorageField.FormatBinaryBytes(volume.FreeBytes)} · {offset}";
            return new StoragePartitionRowView(name, detail, StorageField.FormatBinaryBytes(partition.SizeBytes), free);
        }).ToList();

        if (rows.Count == 0)
        {
            rows.AddRange(report.Volumes.Select(volume => new StoragePartitionRowView(
                string.IsNullOrWhiteSpace(volume.DriveLetter) ? "Volume" : volume.DriveLetter,
                string.Join(" · ", new[] { volume.Label, volume.FileSystem, volume.HealthStatus }.Where(value => !string.IsNullOrWhiteSpace(value))),
                StorageField.FormatBinaryBytes(volume.SizeBytes),
                $"可用 {StorageField.FormatBinaryBytes(volume.FreeBytes)}")));
        }

        PartitionList.ItemsSource = rows;
        NoPartitionsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static StorageVolumeInfo? FindVolume(StoragePartitionInfo partition, IReadOnlyList<StorageVolumeInfo> volumes)
    {
        return volumes.FirstOrDefault(volume => partition.AccessPaths.Any(accessPath =>
            string.Equals(NormalizeStoragePath(accessPath), NormalizeStoragePath(volume.Path), StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(volume.DriveLetter)
                && string.Equals(NormalizeDriveLetter(accessPath), NormalizeDriveLetter(volume.DriveLetter), StringComparison.OrdinalIgnoreCase))));
    }

    private static string FormatAccessPaths(IReadOnlyList<string> accessPaths)
    {
        var display = accessPaths
            .Select(path => NormalizeDriveLetter(path) is { Length: > 0 } driveLetter ? driveLetter : path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return display.Count == 0 ? string.Empty : string.Join(", ", display);
    }

    private static string NormalizeStoragePath(string? value)
    {
        return value?.Trim().TrimEnd('\\') ?? string.Empty;
    }

    private static string NormalizeDriveLetter(string? value)
    {
        var text = value?.Trim().TrimEnd('\\') ?? string.Empty;
        return text.Length >= 2 && text[1] == ':' ? $"{char.ToUpperInvariant(text[0])}:" : string.Empty;
    }

    private static IReadOnlyList<StorageSectionView> BuildSections(StorageDetailReport report)
    {
        return
        [
            new StorageSectionView("设备身份", [
                Row("物理磁盘", report.Identity.PhysicalDriveNumber),
                Row("型号", report.Identity.Model),
                Row("固件", report.Identity.Firmware),
                Row("序列号", report.Identity.SerialNumber),
                Row("容量", report.Identity.Capacity),
                Row("介质类型", report.Identity.MediaType),
                Row("设备路径", report.Identity.DevicePath)
            ]),
            new StorageSectionView("接口与功能", [
                Row("总线", report.Interface.BusType),
                Row("标准", report.Interface.Standard),
                Row("当前链路", report.Interface.CurrentLink),
                Row("最大链路", report.Interface.MaximumLink),
                Row("逻辑扇区", report.Interface.LogicalSectorSize),
                Row("物理扇区", report.Interface.PhysicalSectorSize),
                new StorageFieldRowView("功能", report.Interface.Features.Count == 0 ? StorageField.UnknownText : string.Join(", ", report.Interface.Features), "API", "来自 Windows Storage API。")
            ]),
            new StorageSectionView("使用统计", [
                Row("主机读取", report.Lifetime.HostReads),
                Row("主机写入", report.Lifetime.HostWrites),
                Row("通电次数", report.Lifetime.PowerCycles),
                Row("通电时间", report.Lifetime.PowerOnHours),
                Row("不安全关机", report.Lifetime.UnsafeShutdowns),
                Row("介质错误", report.Lifetime.MediaErrors),
                Row("错误日志项", report.Lifetime.ErrorLogEntries)
            ])
        ];
    }

    private static IReadOnlyList<string> BuildHeaderChips(StorageDetailReport report)
    {
        var chips = new List<string>
        {
            report.Identity.Capacity.DisplayText,
            report.Interface.BusType.DisplayText
        };
        chips.AddRange(report.Interface.Features);
        foreach (var volume in report.Volumes.Select(volume => volume.DriveLetter).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            chips.Add(volume);
        }
        return chips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> BuildNotes(StorageDetailReport report)
    {
        var notes = report.Notes.Select(note => note.Message).ToList();
        notes.AddRange(report.Diagnostics.Providers
            .Where(diagnostic => diagnostic.Error is not null)
            .Select(diagnostic => $"{diagnostic.Provider}: {diagnostic.Error!.Message}"));
        return notes.Distinct(StringComparer.Ordinal).ToList();
    }

    private static StorageFieldRowView Row<T>(string label, StorageFieldValue<T> field)
    {
        return new StorageFieldRowView(label, field.DisplayText, FormatSource(field.Source, field.IsEstimated), DescribeSource(field.Source, field.IsEstimated, field.Note));
    }

    private static string BuildDeviceMeta(StorageDetailReport report)
    {
        var disk = report.Identity.PhysicalDriveNumber.IsAvailable ? $"Disk {report.Identity.PhysicalDriveNumber.DisplayText}" : "Disk ?";
        var volumes = string.Join(' ', report.Volumes.Select(volume => volume.DriveLetter).Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.Join(" · ", new[] { disk, report.Identity.Capacity.DisplayText, report.Interface.BusType.DisplayText, volumes }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string? SelectInitialDevice()
    {
        var devices = App.StorageDetails.Devices;
        return devices.OrderBy(device => device.PhysicalDriveNumber ?? int.MaxValue).FirstOrDefault()?.StableId;
    }

    private void ApplyHealthBrush(StorageHealthStatus status)
    {
        var key = status switch
        {
            StorageHealthStatus.Good => "HwScopeStatusGoodBrush",
            StorageHealthStatus.Caution => "HwScopeStatusCautionBrush",
            StorageHealthStatus.Critical => "HwScopeStatusCriticalBrush",
            _ => "HwScopeStatusUnknownBrush"
        };
        HealthStatusText.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy && _selectedDeviceId is not null;
        CopyButton.IsEnabled = !busy && _currentReport is not null;
        SaveButton.IsEnabled = !busy && _currentReport is not null;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private void Subscribe()
    {
        if (_isSubscribed)
        {
            return;
        }
        App.StorageDetails.DevicesChanged += StorageDetails_DevicesChanged;
        App.StorageDetails.ReportChanged += StorageDetails_ReportChanged;
        _isSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_isSubscribed)
        {
            return;
        }
        App.StorageDetails.DevicesChanged -= StorageDetails_DevicesChanged;
        App.StorageDetails.ReportChanged -= StorageDetails_ReportChanged;
        _isSubscribed = false;
    }

    private void StorageDetails_DevicesChanged(object? sender, EventArgs e)
    {
        if (_selectedDeviceId is not null && App.StorageDetails.Devices.All(device => device.StableId != _selectedDeviceId))
        {
            _selectedDeviceId = SelectInitialDevice();
            _currentReport = null;
        }
        RenderDeviceTiles();
    }

    private void StorageDetails_ReportChanged(object? sender, Services.StorageReportChangedEventArgs e)
    {
        if (e.StableId == _selectedDeviceId && e.Report is not null)
        {
            Render(e.Report);
        }
        else
        {
            RenderDeviceTiles();
        }
    }

    private void SetStatus(string text)
    {
        StatusChanged?.Invoke(this, text);
    }

    private static string FormatSource(StorageDataSource source, bool estimated)
    {
        var label = source switch
        {
            StorageDataSource.Wmi => "WMI",
            StorageDataSource.WindowsStorage => "Storage",
            StorageDataSource.StorageApi => "API",
            StorageDataSource.Nvme => "NVMe",
            StorageDataSource.AtaSmart => "ATA",
            StorageDataSource.Computed => "推导",
            StorageDataSource.Placeholder => "待接入",
            _ => "-"
        };
        return estimated && label != "-" ? $"{label}*" : label;
    }

    private static string DescribeSource(StorageDataSource source, bool estimated, string? note)
    {
        var description = source switch
        {
            StorageDataSource.Wmi => "来自 Windows WMI。",
            StorageDataSource.WindowsStorage => "来自 Windows Storage 管理接口。",
            StorageDataSource.StorageApi => "来自 Windows Storage API。",
            StorageDataSource.Nvme => "来自 NVMe 协议数据。",
            StorageDataSource.AtaSmart => "来自 ATA SMART。",
            StorageDataSource.Computed => "由协议字段推导。",
            StorageDataSource.Placeholder => "当前数据源未提供。",
            _ => "来源未知。"
        };
        if (estimated)
        {
            description += " 带 * 表示推导值。";
        }
        return string.IsNullOrWhiteSpace(note) ? description : $"{description} {note}";
    }

    private enum StorageAttributeFilter
    {
        All,
        Warning,
        Critical
    }
}

public sealed record StorageDeviceTileView(
    string Id,
    string Title,
    string Model,
    StorageHealthStatus Status,
    string StatusText,
    string Temperature,
    string Volumes,
    bool IsSelected);

public sealed record StorageSectionView(string Title, IReadOnlyList<StorageFieldRowView> Rows);

public sealed record StorageFieldRowView(string Label, string Value, string Source, string SourceDescription);

public sealed record StoragePartitionRowView(string Name, string Detail, string Capacity, string Free);

using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Hardware.Memory;
using Microsoft.Win32;

namespace HwScope.App.Pages;

public partial class MemoryDetailPage : UserControl
{
    private readonly MemoryDetailCollector _reportBuilder = new();
    private MemoryDetailReport? _currentReport;
    private string? _selectedModuleId;
    private int _refreshVersion;
    private bool _loadedOnce;
    private bool _isSubscribedToPreload;

    public event EventHandler<string>? StatusChanged;

    public MemoryDetailPage()
    {
        InitializeComponent();
        SetBusy(false);
        Loaded += async (_, _) =>
        {
            SubscribeToPreload();
            if (_loadedOnce)
            {
                return;
            }

            _loadedOnce = true;
            await RefreshAsync(forceRefresh: false);
        };
        Unloaded += (_, _) => UnsubscribeFromPreload();
    }

    public Task RefreshAsync()
    {
        return RefreshAsync(forceRefresh: true);
    }

    private async Task RefreshAsync(bool forceRefresh)
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        SetBusy(true);
        SetStatus("正在读取内存 / SPD 详情...");

        try
        {
            var snapshot = forceRefresh
                ? await App.HardwarePreload.RefreshAsync().ConfigureAwait(true)
                : await App.HardwarePreload.EnsureLoadedAsync().ConfigureAwait(true);
            var report = await Task.Run(() => _reportBuilder.CreateReport(snapshot)).ConfigureAwait(true);
            if (version != _refreshVersion)
            {
                return;
            }

            Render(report);
            SetStatus("内存 / SPD 详情已刷新。");
        }
        catch (Exception ex)
        {
            SetStatus($"内存 / SPD 详情刷新失败：{ex.Message}");
            if (_currentReport is null)
            {
                MemorySubtitleText.Text = "内存信息读取失败，可点击刷新重试。";
                MemorySummaryTitleText.Text = "内存信息读取失败";
                MemorySummaryMetaText.Text = ex.Message;
                System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "内存 / SPD 详情刷新失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null)
        {
            return;
        }

        Clipboard.SetText(MemoryDetailReportFormatter.Format(_currentReport));
        SetStatus("内存 / SPD 详情已复制到剪贴板。");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存内存 / SPD 详情",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-Memory-SPD-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, MemoryDetailReportFormatter.Format(_currentReport));
        SetStatus($"内存 / SPD 详情已保存：{dialog.FileName}");
    }

    private void ModuleTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MemoryModuleTileView tile })
        {
            _selectedModuleId = tile.Id;
            RenderSelectedModule();
        }
    }

    private async void HardwarePreload_InventoryChanged(object? sender, HardwareInventorySnapshot snapshot)
    {
        if (_currentReport is null)
        {
            return;
        }

        try
        {
            var report = await Task.Run(() => _reportBuilder.CreateReport(snapshot)).ConfigureAwait(true);
            Render(report);
        }
        catch (Exception ex)
        {
            SetStatus($"内存 / SPD 详情刷新失败：{ex.Message}");
        }
    }

    private void SubscribeToPreload()
    {
        if (_isSubscribedToPreload)
        {
            return;
        }

        App.HardwarePreload.InventoryChanged += HardwarePreload_InventoryChanged;
        _isSubscribedToPreload = true;
    }

    private void UnsubscribeFromPreload()
    {
        if (!_isSubscribedToPreload)
        {
            return;
        }

        App.HardwarePreload.InventoryChanged -= HardwarePreload_InventoryChanged;
        _isSubscribedToPreload = false;
    }

    private void Render(MemoryDetailReport report)
    {
        _currentReport = report;
        if (_selectedModuleId is null || report.Modules.All(module => module.Id != _selectedModuleId))
        {
            _selectedModuleId = report.Modules.FirstOrDefault()?.Id;
        }

        MemorySummaryTitleText.Text = $"{report.Summary.TotalCapacity.DisplayText} {report.Summary.Type.DisplayText}";
        MemorySummaryMetaText.Text = $"{report.Summary.Layout.DisplayText} · {report.Summary.ConfiguredSpeed.DisplayText} · {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        MemorySubtitleText.Text = $"检测时间：{report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        SummaryChipsList.ItemsSource = BuildSummaryChips(report);
        RuntimeSectionList.ItemsSource = BuildRuntimeSections(report);
        RenderSelectedModule();
        NotesList.ItemsSource = report.Notes.Select(note => note.Message).ToList();
        NotesPanel.Visibility = report.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSelectedModule()
    {
        if (_currentReport is null)
        {
            return;
        }

        var selected = _currentReport.Modules.FirstOrDefault(module => module.Id == _selectedModuleId);
        ModuleTilesList.ItemsSource = _currentReport.Modules.Select(module => ToTile(module, module.Id == _selectedModuleId)).ToList();
        ModuleSectionList.ItemsSource = selected is null ? [] : BuildModuleSections(selected);
        TimingProfilesList.ItemsSource = selected?.TimingProfiles.Select(ToTimingProfileView).ToList() ?? [];
    }

    private static IReadOnlyList<string> BuildSummaryChips(MemoryDetailReport report)
    {
        var chips = new List<string>();
        AddChip(chips, report.Summary.Type);
        AddChip(chips, report.Summary.TotalCapacity);
        AddChip(chips, report.Summary.Layout);
        AddChip(chips, report.Summary.ConfiguredSpeed);
        chips.Add(report.SpdAccess.DisplayText);
        return chips;
    }

    private static IReadOnlyList<MemorySectionView> BuildRuntimeSections(MemoryDetailReport report)
    {
        return
        [
            new MemorySectionView("运行态概览", [
                Row("类型", report.Summary.Type),
                Row("总容量", report.Summary.TotalCapacity),
                Row("模块数", report.Summary.ModuleCount),
                Row("布局", report.Summary.Layout),
                Row("配置速率", report.Summary.ConfiguredSpeed),
                Row("通道模式", report.Summary.ChannelMode)
            ]),
            new MemorySectionView("当前时序", [
                Row("内存频率", report.Runtime.ClockMHz),
                Row("当前有效速率", report.Runtime.EffectiveRate),
                Row("Ratio", report.Runtime.Ratio),
                Row("CAS Latency", report.Runtime.PrimaryTimings.CasLatency),
                Row("tRCD", report.Runtime.PrimaryTimings.Trcd),
                Row("tRP", report.Runtime.PrimaryTimings.Trp),
                Row("tRAS", report.Runtime.PrimaryTimings.Tras),
                Row("tRC", report.Runtime.PrimaryTimings.Trc),
                Row("Command Rate", report.Runtime.PrimaryTimings.CommandRate)
            ])
        ];
    }

    private static IReadOnlyList<MemorySectionView> BuildModuleSections(MemoryModuleDetail module)
    {
        return
        [
            new MemorySectionView("模块身份", [
                Row("插槽", module.Identity.Slot),
                Row("模块名称", module.Identity.DisplayName),
                Row("容量", module.Identity.Capacity),
                Row("模块类型", module.Identity.ModuleType),
                Row("存取类型", module.Identity.MemoryType),
                Row("最大带宽", module.Identity.MaxBandwidth),
                Row("制造商", module.Identity.Manufacturer),
                Row("DRAM 制造商", module.Identity.DramManufacturer),
                Row("Part Number", module.Identity.PartNumber),
                Row("序列号", module.Identity.SerialNumber),
                Row("生产日期", module.Identity.ManufacturingDate),
                Row("Revision", module.Identity.Revision)
            ]),
            new MemorySectionView("模块组织", [
                Row("Rank Mix", module.Organization.RankMix),
                Row("Rank Count", module.Organization.RankCount),
                Row("Bank Groups", module.Organization.BankGroupCount),
                Row("Banks / Group", module.Organization.BanksPerGroup),
                Row("Row Bits", module.Organization.RowAddressBits),
                Row("Column Bits", module.Organization.ColumnAddressBits),
                Row("Device Width", module.Organization.DeviceWidth),
                Row("Bus Width", module.Organization.BusWidth),
                Row("Data Width", module.Organization.DataWidth),
                Row("Total Width", module.Organization.TotalWidth),
                Row("ECC", module.Organization.Ecc),
                Row("On-die ECC", module.Organization.OnDieEcc)
            ]),
            new MemorySectionView("电压和特性", [
                Row("Configured", module.Voltages.ConfiguredVoltage),
                Row("Min Voltage", module.Voltages.MinVoltage),
                Row("Max Voltage", module.Voltages.MaxVoltage),
                Row("VDD", module.Voltages.Vdd),
                Row("VDDQ", module.Voltages.Vddq),
                Row("VPP", module.Voltages.Vpp),
                .. module.Features.Select(feature => Row(feature.Name, feature.Value))
            ]),
            new MemorySectionView("模块说明", BuildModuleNoteRows(module))
        ];
    }

    private static IReadOnlyList<MemoryFieldRowView> BuildModuleNoteRows(MemoryModuleDetail module)
    {
        if (module.Notes.Count == 0)
        {
            return
            [
                new MemoryFieldRowView("状态", "未发现模块级提示。", "-", "无额外模块说明。")
            ];
        }

        return module.Notes
            .Select((note, index) => new MemoryFieldRowView($"提示 {index + 1}", note.Message, FormatSource(note.Source, isEstimated: false), DescribeSource(note.Source, isEstimated: false, note: null)))
            .ToList();
    }

    private static MemoryModuleTileView ToTile(MemoryModuleDetail module, bool isSelected)
    {
        var title = module.Identity.Slot.DisplayText;
        var subtitle = $"{module.Identity.Capacity.DisplayText} {module.Identity.MemoryType.DisplayText} {module.Identity.ModuleType.DisplayText}".Trim();
        var detail = module.Identity.DisplayName.DisplayText;
        var hasWarning = module.Notes.Count > 0;
        var warning = hasWarning ? string.Join(Environment.NewLine, module.Notes.Select(note => note.Message)) : string.Empty;
        return new MemoryModuleTileView(module.Id, title, subtitle, detail, isSelected, hasWarning, warning);
    }

    private static MemoryTimingProfileView ToTimingProfileView(MemoryTimingProfile profile)
    {
        return new MemoryTimingProfileView(
            profile.Name,
            profile.Frequency.DisplayText,
            profile.EffectiveRate.DisplayText,
            profile.CasLatency.DisplayText,
            profile.Trcd.DisplayText,
            profile.Trp.DisplayText,
            profile.Tras.DisplayText,
            profile.Trc.DisplayText,
            profile.Voltage.DisplayText);
    }

    private static MemoryFieldRowView Row<T>(string label, MemoryFieldValue<T> value)
    {
        return new MemoryFieldRowView(label, value.DisplayText, FormatSource(value.Source, value.IsEstimated), DescribeSource(value.Source, value.IsEstimated, value.Note));
    }

    private static void AddChip<T>(ICollection<string> chips, MemoryFieldValue<T> value)
    {
        if (value.IsAvailable)
        {
            chips.Add(value.DisplayText);
        }
    }

    private void SetBusy(bool isBusy)
    {
        RefreshButton.IsEnabled = !isBusy;
        CopyButton.IsEnabled = !isBusy && _currentReport is not null;
        SaveButton.IsEnabled = !isBusy && _currentReport is not null;
        Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;
    }

    private void SetStatus(string text)
    {
        StatusChanged?.Invoke(this, text);
    }

    private static string FormatSource(MemoryDataSource source, bool isEstimated)
    {
        var label = source switch
        {
            MemoryDataSource.Wmi => "WMI",
            MemoryDataSource.Smbios => "SMBIOS",
            MemoryDataSource.Spd => "SPD",
            MemoryDataSource.MemoryController => "控制器",
            MemoryDataSource.Computed => "推导",
            MemoryDataSource.Mapping => "映射",
            MemoryDataSource.Placeholder => "待接入",
            _ => "-"
        };

        return isEstimated && label != "-" ? $"{label}*" : label;
    }

    private static string DescribeSource(MemoryDataSource source, bool isEstimated, string? note)
    {
        var description = source switch
        {
            MemoryDataSource.Wmi => "来自 Windows WMI。",
            MemoryDataSource.Smbios => "来自 SMBIOS。",
            MemoryDataSource.Spd => "来自 SPD。",
            MemoryDataSource.MemoryController => "来自内存控制器运行态读取。",
            MemoryDataSource.Computed => "由已采集字段推导。",
            MemoryDataSource.Mapping => "来自本地映射。",
            MemoryDataSource.Placeholder => "后续阶段接入。",
            _ => "来源未知。"
        };

        if (isEstimated)
        {
            description += " 带 * 表示估算或推导值。";
        }

        return string.IsNullOrWhiteSpace(note) ? description : $"{description} {note}";
    }
}

public sealed record MemorySectionView(string Title, IReadOnlyList<MemoryFieldRowView> Rows);

public sealed record MemoryFieldRowView(string Label, string Value, string Source, string SourceDescription);

public sealed record MemoryModuleTileView(string Id, string Title, string Subtitle, string Detail, bool IsSelected, bool HasWarning, string Warning)
{
    public Visibility WarningVisibility => HasWarning ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record MemoryTimingProfileView(
    string Name,
    string Frequency,
    string EffectiveRate,
    string CasLatency,
    string Trcd,
    string Trp,
    string Tras,
    string Trc,
    string Voltage);

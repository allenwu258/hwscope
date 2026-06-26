using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HwScope.Core.Hardware.Cpu;

namespace HwScope.App.Pages;

public partial class CpuDetailPage : UserControl
{
    private readonly CpuDetailCollector _collector = new();
    private CpuDetailReport? _currentReport;
    private int _refreshVersion;
    private bool _loadedOnce;

    public event EventHandler<string>? StatusChanged;

    public CpuDetailPage()
    {
        InitializeComponent();
        SetBusy(false);
        Loaded += async (_, _) =>
        {
            if (_loadedOnce)
            {
                return;
            }

            _loadedOnce = true;
            await RefreshAsync();
        };
    }

    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        SetBusy(true);
        SetStatus("正在读取 CPU 详情...");

        try
        {
            var report = await Task.Run(_collector.Collect).ConfigureAwait(true);
            if (version != _refreshVersion)
            {
                return;
            }

            _currentReport = report;
            Render(report);
            SetStatus("CPU 详情已刷新。");
        }
        catch (Exception ex)
        {
            SetStatus($"CPU 详情刷新失败：{ex.Message}");
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "CPU 详情刷新失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

        Clipboard.SetText(CpuDetailReportFormatter.Format(_currentReport));
        SetStatus("CPU 详情已复制到剪贴板。");
    }

    private void Render(CpuDetailReport report)
    {
        ProcessorNameText.Text = report.Identity.SpecificationName.DisplayText;
        ProcessorMetaText.Text = $"{report.Identity.Vendor.DisplayText} · {report.Identity.CodeName.DisplayText} · {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        CpuSubtitleText.Text = $"检测时间：{report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";

        HeaderChipsList.ItemsSource = BuildHeaderChips(report);
        SectionList.ItemsSource = BuildSections(report);
        FeatureList.ItemsSource = report.Features
            .Where(feature => feature.IsSupported)
            .Select(feature => new CpuFeatureView(feature.Name))
            .DefaultIfEmpty(new CpuFeatureView(CpuField.PendingCpuidText))
            .ToList();
        NotesList.ItemsSource = report.Notes.Select(note => note.Message).ToList();
        NotesPanel.Visibility = report.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IReadOnlyList<string> BuildHeaderChips(CpuDetailReport report)
    {
        var chips = new List<string>
        {
            $"{report.Topology.CoreCount.DisplayText}C / {report.Topology.LogicalProcessorCount.DisplayText}T"
        };

        AddChip(chips, report.Specification.Package);
        AddChip(chips, report.Specification.Technology);
        AddChip(chips, report.Platform.MemoryType);
        AddChip(chips, report.Clocks.MaxMHz);

        return chips;
    }

    private static IReadOnlyList<CpuSectionView> BuildSections(CpuDetailReport report)
    {
        return
        [
            new CpuSectionView("规格", [
                Row("名称", report.Identity.DisplayName),
                Row("规格", report.Identity.SpecificationName),
                Row("代号", report.Identity.CodeName),
                Row("封装", report.Specification.Package),
                Row("工艺", report.Specification.Technology),
                Row("TDP", report.Specification.Tdp),
                Row("核心电压", report.Specification.CoreVoltage),
                Row("Family", report.Specification.Family),
                Row("Model", report.Specification.Model),
                Row("Stepping", report.Specification.Stepping),
                Row("Ext. Family", report.Specification.ExtendedFamily),
                Row("Ext. Model", report.Specification.ExtendedModel),
                Row("Revision", report.Specification.Revision)
            ]),
            new CpuSectionView("时钟", [
                Row("当前频率", report.Clocks.CurrentMHz),
                Row("基础频率", report.Clocks.BaseMHz),
                Row("最大频率", report.Clocks.MaxMHz),
                Row("总线频率", report.Clocks.BusMHz),
                Row("倍频", report.Clocks.Multiplier)
            ]),
            new CpuSectionView("拓扑", [
                Row("物理处理器", report.Topology.PackageCount),
                Row("核心数", report.Topology.CoreCount),
                Row("线程数", report.Topology.LogicalProcessorCount),
                Row("SMT", report.Topology.SmtEnabled),
                Row("CPU Groups", report.Topology.CpuGroupCount),
                Row("NUMA Nodes", report.Topology.NumaNodeCount)
            ]),
            new CpuSectionView("缓存", report.Caches
                .Select(cache => new CpuFieldRowView(cache.Name, CpuDetailReportFormatter.FormatCache(cache), FormatSource(cache.Source, cache.IsEstimated)))
                .ToList()),
            new CpuSectionView("平台上下文", [
                Row("主板", report.Platform.Motherboard),
                Row("BIOS", report.Platform.BiosVersion),
                Row("芯片组", report.Platform.Chipset),
                Row("集成显卡", report.Platform.IntegratedVideo),
                Row("内存类型", report.Platform.MemoryType),
                Row("内存频率", report.Platform.MemoryClock),
                Row("DRAM:FSB", report.Platform.DramFsbRatio)
            ])
        ];
    }

    private static CpuFieldRowView Row<T>(string label, CpuFieldValue<T> value)
    {
        return new CpuFieldRowView(label, value.DisplayText, FormatSource(value.Source, value.IsEstimated));
    }

    private static void AddChip<T>(ICollection<string> chips, CpuFieldValue<T> value)
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
        Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;
    }

    private void SetStatus(string text)
    {
        StatusChanged?.Invoke(this, text);
    }

    private static string FormatSource(CpuDataSource source, bool isEstimated)
    {
        var label = source switch
        {
            CpuDataSource.Wmi => "WMI",
            CpuDataSource.WindowsApi => "API",
            CpuDataSource.Cpuid => "CPUID",
            CpuDataSource.Mapping => "映射",
            CpuDataSource.Computed => "推导",
            CpuDataSource.Placeholder => "待接入",
            _ => "-"
        };

        return isEstimated && label != "-" ? $"{label}*" : label;
    }
}

public sealed record CpuSectionView(string Title, IReadOnlyList<CpuFieldRowView> Rows);

public sealed record CpuFieldRowView(string Label, string Value, string Source);

public sealed record CpuFeatureView(string Name);

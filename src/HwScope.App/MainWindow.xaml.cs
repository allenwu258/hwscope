using System.Windows;
using System.Windows.Input;
using HwScope.Core.Hardware;

namespace HwScope.App;

public partial class MainWindow : Window
{
    private readonly HardwareCollector _collector = new();
    private HardwareReport? _currentReport;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshHardwareSummary();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHardwareSummary();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null)
        {
            return;
        }

        Clipboard.SetText(HardwareReportFormatter.FormatSummary(_currentReport));
        SetFooterStatus("硬件摘要已复制到剪贴板。");
    }

    private void RefreshHardwareSummary()
    {
        SetBusyState(true);

        try
        {
            _currentReport = _collector.CollectSummary();
            HardwareSummaryList.ItemsSource = HardwareSummaryItem.FromReport(_currentReport);
            GeneratedAtText.Text = $"检测时间：{_currentReport.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
            StatusBadge.Text = "已完成";
            SetFooterStatus("硬件检测完成。");
        }
        catch (Exception ex)
        {
            StatusBadge.Text = "失败";
            SetFooterStatus($"硬件检测失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "硬件检测失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void SetBusyState(bool isBusy)
    {
        RefreshButton.IsEnabled = !isBusy;
        CopyButton.IsEnabled = !isBusy && _currentReport is not null;
        StatusBadge.Text = isBusy ? "检测中" : StatusBadge.Text;
        Mouse.OverrideCursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void SetFooterStatus(string text)
    {
        FooterStatusText.Text = text;
    }
}

public sealed record HardwareSummaryItem(string Label, string Value)
{
    public static IReadOnlyList<HardwareSummaryItem> FromReport(HardwareReport report)
    {
        return
        [
            new("处理器", report.Processor),
            new("主板", report.Motherboard),
            new("内存", report.Memory),
            new("显卡", report.Graphics),
            new("显示器", report.Display),
            new("硬盘", report.Disk),
            new("声卡", string.Join(Environment.NewLine, report.Audio)),
            new("网卡", report.Network),
        ];
    }
}

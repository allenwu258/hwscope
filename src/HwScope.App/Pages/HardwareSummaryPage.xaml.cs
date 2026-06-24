using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HwScope.Core.Hardware;

namespace HwScope.App.Pages;

public partial class HardwareSummaryPage : UserControl
{
    private readonly HardwareCollector _collector = new();
    private HardwareReport? _currentReport;

    public event EventHandler<HardwareReport?>? CurrentReportChanged;
    public event EventHandler<string>? StatusChanged;

    public HardwareSummaryPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_currentReport is null)
            {
                RefreshHardwareSummary();
            }
        };
    }

    public HardwareReport? CurrentReport => _currentReport;

    public void RefreshHardwareSummary()
    {
        SetSummaryBusyState(true);

        try
        {
            _currentReport = _collector.CollectSummary();
            HardwareSummaryList.ItemsSource = HardwareSummaryItem.FromReport(_currentReport);
            GeneratedAtText.Text = $"检测时间：{_currentReport.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
            RaiseCurrentReportChanged();
            SetStatus("硬件检测完成。");
        }
        catch (Exception ex)
        {
            SetStatus($"硬件检测失败：{ex.Message}");
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "硬件检测失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetSummaryBusyState(false);
        }
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
        SetStatus("硬件摘要已复制到剪贴板。");
    }

    private void SetSummaryBusyState(bool isBusy)
    {
        RefreshButton.IsEnabled = !isBusy;
        CopyButton.IsEnabled = !isBusy && _currentReport is not null;
        Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;
    }

    private void SetStatus(string text)
    {
        StatusChanged?.Invoke(this, text);
    }

    private void RaiseCurrentReportChanged()
    {
        CurrentReportChanged?.Invoke(this, _currentReport);
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
